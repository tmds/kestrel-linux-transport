using System;
using System.Buffers;
using System.Collections;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Protocols;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    sealed partial class TransportThread
    {
        [Flags]
        enum SocketFlags
        {
            None            = 0,

            AwaitReadable = 0x01,    // EPOLLIN
            AwaitWritable = 0x04,    // EPOLLOUT
            AwaitZeroCopy = 0x08,    // EPOLLERR
            EventControlRegistered = 0x10, // EPOLLHUP
            EventControlPending = 1 << 30, // EPOLLONESHOT

            CloseEnd        = 0x20,
            BothClosed      = 0x40,

            TypeAccept      = 0x100,
            TypeClient      = 0x200,
            TypePassFd      = 0x300,
            TypeMask        = 0x300,

            DeferAccept     = 0x400,
            WriteStopped    = 0x1000,
            ReadStopped     = 0x2000
        }

        class TSocket : TransportConnection
        {
            public int ZeroCopyThreshold;
            public readonly object Gate = new object();
            public ThreadContext ThreadContext;
            public int         Fd;
            public Socket      Socket;
            private int _flags;
            private Exception   _outputCompleteError;
            private Exception _inputCompleteError;
            private PipeAwaiter<ReadResult> _readAwaiter;
            private PipeAwaiter<FlushResult> _flushAwaiter;
            private int _zeropCopyState;
            private SequencePosition _zeroCopyEnd;
            private readonly Action _onFlushedToApp;
            private readonly Action _onReadFromApp;

            private const int ZeroCopyNone = 0;
            private const int ZeroCopyComplete = 1;
            private const int ZeroCopyAwait = 2;
            private const EPollEvents EventControlRegistered = (EPollEvents)SocketFlags.EventControlRegistered;
            public const EPollEvents EventControlPending = (EPollEvents)SocketFlags.EventControlPending;

            public TSocket(ThreadContext threadContext, SocketFlags flags)
            {
                ThreadContext = threadContext;
                _flags = (int)flags;
                _onFlushedToApp = new Action(OnFlushedToApp);
                _onReadFromApp = new Action(OnReadFromApp);
            }

            public SocketFlags Flags
            {
                get { return (SocketFlags)_flags; }
            }

            public SocketFlags Type => ((SocketFlags)_flags & SocketFlags.TypeMask);

            // must be called under Gate
            public EPollEvents PendingEventState
            {
                get => (EPollEvents)_flags;
                set => _flags = (int)value;
            }

            private void StopWriteToSocket()
            {
                bool completeWritable = false;
                lock (Gate)
                {
                    var flags = Flags;
                    if ((flags & SocketFlags.WriteStopped) != SocketFlags.None)
                    {
                        return;
                    }
                    if ((Flags & SocketFlags.AwaitWritable) != SocketFlags.None)
                    {
                        completeWritable = true;
                    }
                    if ((Flags & SocketFlags.AwaitZeroCopy) != SocketFlags.None)
                    {
                        // Terminate pending zero copy
                        // Call it under Gate so it doesn't race with Close
                        SocketInterop.Disconnect(Fd);
                    }
                    flags &= ~SocketFlags.AwaitWritable;
                    flags |= SocketFlags.WriteStopped;
                    _flags = (int)flags;
                }
                if (completeWritable)
                {
                    OnWritable(stopped: true);
                }
            }

            private void StopReadFromSocket(Exception exception)
            {
                bool completeReadable = false;
                lock (Gate)
                {
                    var flags = Flags;
                    if ((flags & SocketFlags.ReadStopped) != SocketFlags.None)
                    {
                        return;
                    }
                    if ((Flags & SocketFlags.AwaitReadable) != SocketFlags.None)
                    {
                        completeReadable = true;
                    }
                    flags &= ~SocketFlags.AwaitReadable;
                    flags |= SocketFlags.ReadStopped;
                    _inputCompleteError = exception ?? TransportThread.EofSentinel;
                    _flags = (int)flags;
                }
                if (completeReadable)
                {
                    OnReceiveFromSocket(_inputCompleteError);
                }
            }

            private void WriteToSocket()
            {
                Output.OnWriterCompleted(CompleteWriteToSocket, this);
                ReadFromApp();
            }

            private void ReadFromApp()
            {
                bool loop = true;
                do
                {
                    _readAwaiter = Output.ReadAsync();
                    if (_readAwaiter.IsCompleted)
                    {
                        loop = OnReadFromApp(loop);
                    }
                    else
                    {
                        _readAwaiter.UnsafeOnCompleted(_onReadFromApp);
                        loop = false;
                    }
                } while (loop);
            }

            private void OnReadFromApp()
            {
                OnReadFromApp(loop: false);
            }

            private bool OnReadFromApp(bool loop)
            {
                Exception error = null;
                bool stop = false;
                try
                {
                    var readResult = _readAwaiter.GetResult();
                    ReadOnlySequence<byte> buffer = readResult.Buffer;
                    SequencePosition end = buffer.Start;
                    bool zerocopy = false;
                    bool zeroCopyRegistered = false;
                    if ((buffer.IsEmpty && readResult.IsCompleted) || readResult.IsCanceled)
                    {
                        // EOF or TransportThread stopped
                        stop = true;
                    }
                    else if (!buffer.IsEmpty)
                    {
                        zerocopy = buffer.Length >= ZeroCopyThreshold;
                        PosixResult result;
                        (result, zeroCopyRegistered) = TrySend(zerocopy, ref buffer);
                        if (result.Value == buffer.Length)
                        {
                            end = buffer.End;
                        }
                        else if (result.IsSuccess)
                        {
                            end = buffer.GetPosition(buffer.Start, result.Value);
                        }
                        else if (result == PosixResult.EAGAIN || result == PosixResult.EWOULDBLOCK)
                        {
                            Output.AdvanceTo(end);
                            WaitSocketWritable();
                            return false;
                        }
                        else if (zerocopy && result == PosixResult.ENOBUFS)
                        {
                            // We reached the max locked memory (ulimit -l), disable zerocopy.
                            ZeroCopyThreshold = LinuxTransportOptions.NoZeroCopy;
                        }
                        else
                        {
                            error = result.AsException();
                            stop = true;
                        }
                        if (zerocopy)
                        {
                            if (result.Value > 0)
                            {
                                _zeroCopyEnd = end;
                            }
                            else
                            {
                                zerocopy = false;
                            }
                        }
                    }
                    if (zerocopy)
                    {
                        return WaitZeroCopyComplete(loop, zeroCopyRegistered);
                    }
                    else
                    {
                        // We need to call Advance to end the read
                        Output.AdvanceTo(end);
                    }
                }
                catch (Exception e)
                {
                    stop = true;
                    error = e;
                }
                if (stop)
                {
                    CompleteOutput(error);
                    loop = false;
                }
                else
                {
                    if (!loop)
                    {
                        ReadFromApp();
                    }
                }
                return loop;
            }

            private bool WaitZeroCopyComplete(bool loop, bool registered)
            {
                if (registered)
                {
                    int previousState = Interlocked.CompareExchange(ref _zeropCopyState, ZeroCopyAwait, ZeroCopyNone);
                    if (previousState == ZeroCopyComplete)
                    {
                        // registered, complete
                        Volatile.Write(ref _zeropCopyState, ZeroCopyNone);
                        Output.AdvanceTo(_zeroCopyEnd);
                        _zeroCopyEnd = default(SequencePosition);
                        if (!loop)
                        {
                            ReadFromApp();
                        }
                        return loop;
                    }
                    else
                    {
                        // registered, not completed
                        return false;
                    }
                }
                else
                {
                    // not registered
                    lock (Gate)
                    {
                        RegisterFor(EPollEvents.Error);
                    }
                    return false;
                }
            }

            public void CompleteZeroCopy()
            {
                int previousState = Interlocked.CompareExchange(ref _zeropCopyState, ZeroCopyAwait, ZeroCopyNone);
                if (previousState == ZeroCopyAwait)
                {
                    Volatile.Write(ref _zeropCopyState, ZeroCopyNone);
                    Output.AdvanceTo(_zeroCopyEnd);
                    _zeroCopyEnd = default(SequencePosition);
                    ReadFromApp();
                }
            }

            private void CompleteOutput(Exception e)
            {
                _outputCompleteError = e;
                StopReadFromSocket(e);
                CleanupSocketEnd();
            }

            private void WaitSocketWritable()
            {
                bool stopped = false;
                lock (Gate)
                {
                    stopped = (Flags & SocketFlags.WriteStopped) != SocketFlags.None;
                    if (!stopped)
                    {
                        RegisterFor(EPollEvents.Writable);
                    }
                }
                if (stopped)
                {
                    OnWritable(true);
                }
            }

            public void OnWritable(bool stopped)
            {
                if (stopped)
                {
                    CompleteOutput(null);
                }
                else
                {
                    ReadFromApp();
                }
            }

            private void RegisterFor(EPollEvents ev)
            {
                // called under tsocket.Gate
                var pendingEventState = PendingEventState;
                bool registered = (pendingEventState & TSocket.EventControlRegistered) != EPollEvents.None;
                pendingEventState |= TSocket.EventControlRegistered | ev;
                PendingEventState = pendingEventState;

                if ((pendingEventState & TSocket.EventControlPending) == EPollEvents.None)
                {
                    TransportThread.UpdateEPollControl(this, pendingEventState, registered);
                }
            }

            private static void CompleteWriteToSocket(Exception ex, object state)
            {
                if (ex != null)
                {
                    var tsocket = (TSocket)state;
                    tsocket.StopWriteToSocket();
                }
            }

            private void CleanupSocketEnd()
            {
                lock (Gate)
                {
                    _flags = _flags + (int)SocketFlags.CloseEnd;
                    if ((_flags & (int)SocketFlags.BothClosed) != 0)
                    {
                        return;
                    }
                }

                Output.Complete(_outputCompleteError);

                // First remove from the Dictionary, so we can't match with a new fd.
                ThreadContext.RemoveSocket(Fd);

                // We are not using SafeHandles to increase performance.
                // We get here when both reading and writing has stopped
                // so we are sure this is the last use of the Socket.
                Socket.Dispose();
            }

            public static unsafe int FillReceiveIOVector(PipeWriter writer, int availableBytes, IOVector* ioVectors, ref int ioVectorLength)
            {
                Memory<byte> memory = writer.GetMemory(2048);
                var allocated = 0;

                var advanced = 0;
                int ioVectorsUsed = 0;
                for (; ioVectorsUsed < ioVectorLength; ioVectorsUsed++)
                {
                    var length = memory.Length;
                    var bufferHandle = memory.Retain(pin: true);
                    ioVectors[ioVectorsUsed].Base = bufferHandle.Pointer;
                    ioVectors[ioVectorsUsed].Count = (void*)length;
                    // It's ok to unpin the handle here because the memory is from the pool
                    // we created, which is already pinned.
                    bufferHandle.Dispose();
                    allocated += length;

                    if (allocated >= availableBytes)
                    {
                        // Every Memory (except the last one) must be filled completely.
                        ioVectorsUsed++;
                        break;
                    }

                    writer.Advance(length);
                    advanced += length;
                    memory = writer.GetMemory(1);
                }
                ioVectorLength = ioVectorsUsed;
                return advanced;
            }

            public static int CalcIoVectorLength(PipeWriter writer, int availableBytes)
            {
                Memory<byte> memory = writer.GetMemory(2048);
                return availableBytes <= memory.Length ? 1 :
                       Math.Min(1 + (availableBytes - memory.Length + MaxPooledBlockLength - 1) / MaxPooledBlockLength, MaxIOVectorReceiveLength);
            }

            public unsafe Exception Receive(int availableBytes = 0)
            {
                PipeWriter writer = Input;
                int ioVectorLength = CalcIoVectorLength(writer, availableBytes);
                var ioVectors = stackalloc IOVector[ioVectorLength];
                int advanced = FillReceiveIOVector(writer, availableBytes, ioVectors, ref ioVectorLength);

                // Ideally we get availableBytes in a single receive
                // but we are happy if we get at least a part of it
                // and we are willing to take {MaxEAgainCount} EAGAINs.
                // Less data could be returned due to these reasons:
                // * TCP URG
                // * packet was not placed in receive queue (race with FIONREAD)
                // * ?
                const int MaxEAgainCount = 10;
                var eAgainCount = 0;
                var received = 0;
                do
                {
                    var result = SocketInterop.Receive(Fd, ioVectors, ioVectorLength);
                    (bool done, Exception retval) = InterpretReceiveResult(writer, result, ref received, advanced, ioVectors, ioVectorLength);
                    if (done)
                    {
                        return retval;
                    }
                    else if (retval == EAgainSentinel)
                    {
                        if (availableBytes == 0)
                        {
                            return EAgainSentinel;
                        }
                        eAgainCount++;
                        if (eAgainCount == MaxEAgainCount)
                        {
                            return new NotSupportedException("Too many EAGAIN, unable to receive available bytes.");
                        }
                    }
                    else
                    {
                        eAgainCount = 0;
                    }
                } while (true);
            }

            private static unsafe (bool done, Exception receiveResult) InterpretReceiveResult(PipeWriter writer, PosixResult result, ref int received, int advanced, IOVector* ioVectors, int ioVectorLength)
            {
                    if (result.IsSuccess)
                    {
                        received += result.Value;
                        if (received >= advanced)
                        {
                            // We made it!
                            writer.Advance(received - advanced);
                            return (true, received == 0 ? EofSentinel : null);
                        }
                        // Update ioVectors to match bytes read
                        var skip = result.Value;
                        for (int i = 0; (i < ioVectorLength) && (skip > 0); i++)
                        {
                            var length = (int)ioVectors[i].Count;
                            var skipped = Math.Min(skip, length);
                            ioVectors[i].Count = (void*)(length - skipped);
                            ioVectors[i].Base = (byte*)ioVectors[i].Base + skipped;
                            skip -= skipped;
                        }
                        return (false, null);
                    }
                    else if (result == PosixResult.EAGAIN || result == PosixResult.EWOULDBLOCK)
                    {
                        return (false, EAgainSentinel);
                    }
                    else if (result == PosixResult.ECONNRESET)
                    {
                        return (true, new ConnectionResetException(result.ErrorDescription(), result.AsException()));
                    }
                    else
                    {
                        return (true, result.AsException());
                    }
            }

            private void ReceiveFromSocket()
            {
                bool stopped = false;
                lock (Gate)
                {
                    stopped = (Flags & SocketFlags.ReadStopped) != SocketFlags.None;
                    if (!stopped)
                    {
                        RegisterFor(EPollEvents.Readable);
                    }
                }
                if (stopped)
                {
                    OnReceiveFromSocket(_inputCompleteError);
                }
            }

            public void OnReceiveFromSocket(Exception result)
            {
                if (result == null)
                {
                    FlushToApp();
                }
                else if (result == EAgainSentinel)
                {
                    ReceiveFromSocket();
                }
                else if (result == EofSentinel)
                {
                    CompleteInput(null);
                }
                else
                {
                    CompleteInput(result);
                }
            }

            private void FlushToApp()
            {
                _flushAwaiter = Input.FlushAsync();
                if (_flushAwaiter.IsCompleted)
                {
                    OnFlushedToApp();
                }
                else
                {
                    _flushAwaiter.UnsafeOnCompleted(_onFlushedToApp);
                }
            }

            private void OnFlushedToApp()
            {
                Exception error = null;
                try
                {
                    FlushResult flushResult = _flushAwaiter.GetResult();
                    if (flushResult.IsCompleted || // Reader has stopped
                        flushResult.IsCanceled)   // TransportThread has stopped
                    {
                        error = new ConnectionAbortedException();
                    }
                }
                catch (Exception e)
                {
                    error = e;
                }
                if (error == null)
                {
                    ReceiveFromSocket();
                }
                else
                {
                    CompleteInput(error);
                }
            }

            private void CompleteInput(Exception error)
            {
                Input.Complete(error);

                CleanupSocketEnd();
            }

            private unsafe (PosixResult, bool zerocopyRegistered) TrySend(bool zerocopy, ref ReadOnlySequence<byte> buffer)
            {
                bool zeroCopyRegistered = false;
                int ioVectorLength = 0;
                foreach (var memory in buffer)
                {
                    if (memory.Length == 0)
                    {
                        continue;
                    }
                    ioVectorLength++;
                    if (ioVectorLength == MaxIOVectorSendLength)
                    {
                        // No more room in the IOVector
                        break;
                    }
                }
                if (ioVectorLength == 0)
                {
                    return (new PosixResult(0), zeroCopyRegistered);
                }

                var ioVectors = stackalloc IOVector[ioVectorLength];
                int i = 0;
                foreach (var memory in buffer)
                {
                    if (memory.Length == 0)
                    {
                        continue;
                    }
                    var bufferHandle = memory.Retain(pin: true);
                    ioVectors[i].Base = bufferHandle.Pointer;
                    // It's ok to unpin the handle here because the memory is from the pool
                    // we created, which is already pinned.
                    bufferHandle.Dispose();
                    ioVectors[i].Count = (void*)memory.Length;
                    i++;
                    if (i == ioVectorLength)
                    {
                        // No more room in the IOVector
                        break;
                    }
                }

                if (zerocopy)
                {
                    lock (Gate)
                    {
                        // Don't start new zerocopies when writting stopped.
                        if ((Flags & SocketFlags.WriteStopped) != SocketFlags.None)
                        {
                            return (new PosixResult(PosixResult.ECONNABORTED), zeroCopyRegistered);
                        }

                        // If we have a pending Readable event, it will report on the zero-copy completion too.
                        if ((PendingEventState & EPollEvents.Readable) != EPollEvents.None)
                        {
                            PendingEventState |= EPollEvents.Error;
                            zeroCopyRegistered = true;
                        }
                    }
                }

                PosixResult rv = SocketInterop.Send(Fd, ioVectors, ioVectorLength, zerocopy ? MSG_ZEROCOPY : 0);

                if (zerocopy && rv.Value <= 0 && zeroCopyRegistered)
                {
                    lock (Gate)
                    {
                        PendingEventState &= ~EPollEvents.Error;
                    }
                    zeroCopyRegistered = false;
                }

                return (rv, zeroCopyRegistered);
            }

            public void Start(bool dataMayBeAvailable)
            {
                WriteToSocket();
                // TODO: implement dataMayBeAvailable
                ReceiveFromSocket();
            }

            public void Stop()
            {
                StopWriteToSocket();
            }

            public override MemoryPool<byte> MemoryPool => ThreadContext.MemoryPool;

            public override PipeScheduler InputWriterScheduler => PipeScheduler.Inline;

            public override PipeScheduler OutputReaderScheduler => ThreadContext.SendScheduler;
        }
    }
}