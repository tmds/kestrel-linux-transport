using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Sequences;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Protocols;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;
using Microsoft.Extensions.Logging;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    sealed partial class TransportThread : ITransportActionHandler
    {
        private const int MSG_ZEROCOPY = 0x4000000;
        // 128 IOVectors, take up 2KB of stack, can send up to 512KB
        private const int MaxIOVectorSendLength = 128;
        // 128 IOVectors, take up 2KB of stack, can receive up to 512KB
        private const int MaxIOVectorReceiveLength = 128;
        private const int ListenBacklog     = 128;
        private const int EventBufferLength = 512;
        private const int EPollBlocked      = 1;
        private const int EPollNotBlocked   = 0;
        private const byte PipeStopThread     = 0;
        private const byte PipeActionsPending = 1;
        private const byte PipeStopSockets    = 2;

        private static readonly int MaxPooledBlockLength;
        private static readonly int MaxSendLength;
        static TransportThread()
        {
            using (var memoryPool = new MemoryPool())
            {
                MaxPooledBlockLength = memoryPool.MaxBufferSize;
                MaxSendLength = MaxIOVectorSendLength * MaxPooledBlockLength;
            }
        }

        private struct ScheduledAction
        {
            public Action<object> Action;
            public Object State;
        }

        enum State
        {
            Initial,
            Starting,
            Started,
            ClosingAccept,
            AcceptClosed,
            Stopping,
            Stopped
        }

        private readonly IConnectionHandler _connectionHandler;
        private readonly int _threadId;
        private readonly IPEndPoint _endPoint;
        private readonly LinuxTransportOptions _transportOptions;
        private readonly AcceptThread _acceptThread;
        private readonly ILoggerFactory _loggerFactory;
        private readonly object _gate = new object();
        private int _cpuId;
        private State _state;
        private Thread _thread;
        private TaskCompletionSource<object> _stateChangeCompletion;
        private ThreadContext _threadContext;

        public TransportThread(IPEndPoint endPoint, IConnectionHandler connectionHandler, LinuxTransportOptions options, AcceptThread acceptThread, int threadId, int cpuId, ILoggerFactory loggerFactory)
        {
            if (connectionHandler == null)
            {
                throw new ArgumentNullException(nameof(connectionHandler));
            }
            _connectionHandler = connectionHandler;
            _threadId = threadId;
            _cpuId = cpuId;
            _endPoint = endPoint;
            _transportOptions = options;
            _acceptThread = acceptThread;
            _loggerFactory = loggerFactory;
        }

        public Task BindAsync()
        {
            TaskCompletionSource<object> tcs;
            lock (_gate)
            {
                if (_state == State.Started)
                {
                    return Task.CompletedTask;
                }
                else if (_state == State.Starting)
                {
                    return _stateChangeCompletion.Task;
                }
                else if (_state != State.Initial)
                {
                    ThrowInvalidState();
                }
                try
                {
                    tcs = _stateChangeCompletion = new TaskCompletionSource<object>();
                    _state = State.Starting;

                    _thread = new Thread(PollThread);
                    _thread.Start();
                }
                catch
                {
                    _state = State.Stopped;
                    throw;
                }
            }
            return tcs.Task;
        }

        private static Socket CreateAcceptSocket(IPEndPoint endPoint, LinuxTransportOptions transportOptions, int cpuId, ThreadContext threadContext, ref SocketFlags flags, out int zeroCopyThreshold)
        {
            Socket acceptSocket = null;
            int port = endPoint.Port;
            try
            {
                bool ipv4 = endPoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
                acceptSocket = Socket.Create(ipv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp, blocking: false);
                if (!ipv4)
                {
                    // Kestrel does mapped ipv4 by default.
                    acceptSocket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, 0);
                }
                if (transportOptions.ReceiveOnIncomingCpu)
                {
                    if (cpuId != -1)
                    {
                        if (!acceptSocket.TrySetSocketOption(SocketOptionLevel.Socket, SocketOptionName.IncomingCpu, cpuId))
                        {
                            threadContext.Logger.LogWarning($"Cannot enable nameof{SocketOptionName.IncomingCpu} for {endPoint}");
                        }
                    }
                }
                // Linux: allow bind during linger time
                acceptSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
                // Linux: allow concurrent binds and let the kernel do load-balancing
                acceptSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReusePort, 1);
                if (transportOptions.DeferAccept)
                {
                    // Linux: wait up to 1 sec for data to arrive before accepting socket
                    acceptSocket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.DeferAccept, 1);
                    flags |= SocketFlags.DeferAccept;
                }
                zeroCopyThreshold = LinuxTransportOptions.NoZeroCopy;
                if (transportOptions.ZeroCopy && transportOptions.ZeroCopyThreshold != LinuxTransportOptions.NoZeroCopy)
                {
                    if (acceptSocket.TrySetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ZeroCopy, 1))
                    {
                        zeroCopyThreshold = transportOptions.ZeroCopyThreshold;
                    }
                }

                acceptSocket.Bind(endPoint);
                if (port == 0)
                {
                    // When testing we want the OS to select a free port
                    port = acceptSocket.GetLocalIPAddress().Port;
                }

                acceptSocket.Listen(ListenBacklog);

                endPoint.Port = port;
                return acceptSocket;
            }
            catch
            {
                acceptSocket?.Dispose();
                throw;
            }
        }

        private static void AcceptOn(Socket acceptSocket, SocketFlags flags, int zeroCopyThreshold, ThreadContext threadContext)
        {
            TSocket tsocket = null;
            int fd = acceptSocket.DangerousGetHandle().ToInt32();
            var sockets = threadContext.Sockets;
            try
            {
                tsocket = new TSocket(threadContext, flags)
                {
                    Fd = fd,
                    Socket = acceptSocket,
                    ZeroCopyThreshold = zeroCopyThreshold
                };
                threadContext.AcceptSockets.Add(tsocket);
                lock (sockets)
                {
                    sockets.Add(tsocket.Fd, tsocket);
                }

                EPollInterop.EPollControl(threadContext.EPollFd,
                                          EPollOperation.Add,
                                          fd,
                                          EPollEvents.Readable,
                                          EPollData(fd));
            }
            catch
            {
                acceptSocket.Dispose();
                threadContext.AcceptSockets.Remove(tsocket);
                lock (sockets)
                {
                    sockets.Remove(fd);
                }
                throw;
            }
        }

        public async Task UnbindAsync()
        {
            TaskCompletionSource<object> tcs = null;
            lock (_gate)
            {
                if (_state == State.Initial)
                {
                    _state = State.Stopped;
                    return;
                }
                else if (_state == State.AcceptClosed || _state == State.Stopping || _state == State.Stopped)
                {
                    return;
                }
                else if (_state == State.ClosingAccept)
                {
                    tcs = _stateChangeCompletion;
                }
            }
            if (tcs != null)
            {
                await tcs.Task;
                return;
            }
            try
            {
                await BindAsync();
            }
            catch
            {}
            bool triggerStateChange = false;
            lock (_gate)
            {
                if (_state == State.AcceptClosed || _state == State.Stopping || _state == State.Stopped)
                {
                    return;
                }
                else if (_state == State.ClosingAccept)
                {
                    tcs = _stateChangeCompletion;
                }
                else if (_state == State.Started)
                {
                    triggerStateChange = true;
                    tcs = _stateChangeCompletion = new TaskCompletionSource<object>();
                    _state = State.ClosingAccept;
                }
                else
                {
                    // Cannot happen
                    ThrowInvalidState();
                }
            }
            if (triggerStateChange)
            {
                _threadContext.CloseAccept();
            }
            await tcs.Task;
        }

        public async Task StopAsync()
        {
            lock (_gate)
            {
                if (_state == State.Initial)
                {
                    _state = State.Stopped;
                    return;
                }
            }

            await UnbindAsync();

            TaskCompletionSource<object> tcs = null;
            bool triggerStateChange = false;
            lock (_gate)
            {
                if (_state == State.Stopped)
                {
                    return;
                }
                else if (_state == State.Stopping)
                {
                    tcs = _stateChangeCompletion;
                }
                else if (_state == State.AcceptClosed)
                {
                    tcs = _stateChangeCompletion = new TaskCompletionSource<object>();
                    _state = State.Stopping;
                    triggerStateChange = true;
                }
                else
                {
                    // Cannot happen
                    ThrowInvalidState();
                }
            }
            if (triggerStateChange)
            {
                _threadContext.StopSockets();
            }
            await tcs.Task;
        }

        private ILogger CreateLogger()
        {
            return _loggerFactory.CreateLogger($"{nameof(TransportThread)}.{_threadId}");
        }

        private unsafe void PollThread(object obj)
        {
            ThreadContext threadContext = null;
            try
            {
                // .NET doesn't support setting thread affinity on Start
                // We could change it before starting the thread
                // so it gets inherited, but we don't know how many threads
                // the runtime may start.
                if (_cpuId != -1)
                {
                    SystemScheduler.SetCurrentThreadAffinity(_cpuId);
                }
                // objects are allocated on the PollThread heap
                int pipeKey;
                threadContext = new ThreadContext(this, _transportOptions, _connectionHandler, CreateLogger());
                threadContext.Initialize();
                {
                    // register pipe 
                    pipeKey = threadContext.PipeEnds.ReadEnd.DangerousGetHandle().ToInt32();
                    EPollInterop.EPollControl(threadContext.EPollFd,
                                            EPollOperation.Add,
                                            threadContext.PipeEnds.ReadEnd.DangerousGetHandle().ToInt32(),
                                            EPollEvents.Readable,
                                            EPollData(pipeKey));

                    Socket acceptSocket;
                    SocketFlags flags;
                    int zeroCopyThreshold;
                    if (_acceptThread != null)
                    {
                        flags = SocketFlags.TypePassFd;
                        acceptSocket = _acceptThread.CreateReceiveSocket();
                        zeroCopyThreshold = LinuxTransportOptions.NoZeroCopy;
                    }
                    else
                    {
                        flags = SocketFlags.TypeAccept;
                        acceptSocket = CreateAcceptSocket(_endPoint, _transportOptions, _cpuId, threadContext, ref flags, out zeroCopyThreshold);
                    }
                    // accept connections
                    AcceptOn(acceptSocket, flags, zeroCopyThreshold, threadContext);

                    _threadContext = threadContext;
                }
                int epollFd = threadContext.EPollFd;
                var readEnd = threadContext.PipeEnds.ReadEnd;
                int notPacked = !EPoll.PackedEvents ? 1 : 0;
                var buffer = stackalloc int[EventBufferLength * (3 + notPacked)];
                int statReadEvents = 0;
                int statWriteEvents = 0;
                int statAcceptEvents = 0;
                int statAccepts = 0;
                int statZeroCopySuccess = 0;
                int statZeroCopyCopied = 0;
                var sockets = threadContext.Sockets;
                bool checkAvailable = _transportOptions.CheckAvailable;

                var acceptableSockets = new List<TSocket>(1);
                var readableSockets = new List<TSocket>(EventBufferLength);
                var writableSockets = new List<TSocket>(EventBufferLength);
                var reregisterEventSockets = new List<TSocket>(EventBufferLength);
                var zeroCopyCompletions = new List<TSocket>(EventBufferLength);
                bool pipeReadable = false;

                CompleteStateChange(State.Started);
                bool running = true;
                do
                {
                    int numEvents = EPollInterop.EPollWait(epollFd, buffer, EventBufferLength, timeout: EPoll.TimeoutInfinite).Value;

                    // actions can be scheduled without unblocking epoll
                    threadContext.SetEpollNotBlocked();

                    // check events
                    // we don't handle them immediately:
                    // - this ensures we don't mismatch a closed socket with a new socket that have the same fd
                    //     ~ To have the same fd, the previous fd must be closed, which means it is removed from the epoll
                    //     ~ and won't show up in our next call to epoll.Wait.
                    //     ~ The old fd may be present in the buffer still, but lookup won't give a match, since it is removed
                    //     ~ from the dictionary before it is closed. If we were accepting already, a new socket could match.
                    // - this also improves cache/cpu locality of the lookup
                    int* ptr = buffer;
                    lock (sockets)
                    {
                        for (int i = 0; i < numEvents; i++)
                        {
                            //   Packed             Non-Packed
                            //   ------             ------
                            // 0:Events       ==    Events
                            // 1:Int1 = Key         [Padding]
                            // 2:Int2 = Key   ==    Int1 = Key
                            // 3:~~~~~~~~~~         Int2 = Key
                            //                      ~~~~~~~~~~
                            EPollEvents events = (EPollEvents)ptr[0];
                            int key = ptr[2];
                            ptr += 3 + notPacked;
                            TSocket tsocket;
                            if (sockets.TryGetValue(key, out tsocket))
                            {
                                var type = tsocket.Type;
                                if (type == SocketFlags.TypeClient)
                                {
                                    lock (tsocket.Gate)
                                    {
                                        var pendingEventState = tsocket.PendingEventState;

                                        // zero copy
                                        if ((pendingEventState & EPollEvents.Error & events) != EPollEvents.None)
                                        {
                                            var copyResult = SocketInterop.CompleteZeroCopy(tsocket.Fd);
                                            if (copyResult != PosixResult.EAGAIN)
                                            {
                                                events &= ~EPollEvents.Error;
                                                pendingEventState &= ~EPollEvents.Error;
                                                zeroCopyCompletions.Add(tsocket);
                                                if (copyResult == SocketInterop.ZeroCopyCopied)
                                                {
                                                    tsocket.ZeroCopyThreshold = LinuxTransportOptions.NoZeroCopy;
                                                    statZeroCopyCopied++;
                                                }
                                                else if (copyResult == SocketInterop.ZeroCopySuccess)
                                                {
                                                    statZeroCopySuccess++;
                                                }
                                                else
                                                {
                                                    Environment.FailFast($"Error occurred while trying to complete zero copy: {copyResult}");
                                                }
                                            }
                                        }

                                        // treat Error as Readable, Writable
                                        if ((events & EPollEvents.Error) != EPollEvents.None)
                                        {
                                            events |= EPollEvents.Readable | EPollEvents.Writable;
                                        }

                                        events &= pendingEventState & (EPollEvents.Readable | EPollEvents.Writable);
                                        // readable
                                        if ((events & EPollEvents.Readable) != EPollEvents.None)
                                        {
                                            readableSockets.Add(tsocket);
                                            pendingEventState &= ~EPollEvents.Readable;
                                        }
                                        // writable
                                        if ((events & EPollEvents.Writable) != EPollEvents.None)
                                        {
                                            writableSockets.Add(tsocket);
                                            pendingEventState &= ~EPollEvents.Writable;
                                        }

                                        // reregister
                                        tsocket.PendingEventState = pendingEventState;
                                        if ((pendingEventState & (EPollEvents.Readable | EPollEvents.Writable)) != EPollEvents.None)
                                        {
                                            tsocket.PendingEventState |= TSocket.EventControlPending;
                                            reregisterEventSockets.Add(tsocket);
                                        }
                                    }
                                }
                                else
                                {
                                    statAcceptEvents++;
                                    acceptableSockets.Add(tsocket);
                                }
                            }
                            else if (key == pipeKey)
                            {
                                pipeReadable = true;
                            }
                        }
                    }

                    // zero copy
                    for (int i = 0; i < zeroCopyCompletions.Count; i++)
                    {
                        zeroCopyCompletions[i].CompleteZeroCopy();
                    }
                    zeroCopyCompletions.Clear();

                    // handle accepts
                    statAcceptEvents += acceptableSockets.Count;
                    for (int i = 0; i < acceptableSockets.Count; i++)
                    {
                        statAccepts += HandleAccept(acceptableSockets[i], threadContext);
                    }
                    acceptableSockets.Clear();

                    // handle writes
                    statWriteEvents += writableSockets.Count;
                    for (int i = 0; i < writableSockets.Count; i++)
                    {
                        writableSockets[i].OnWritable(stopped: false);
                    }
                    writableSockets.Clear();

                    // handle reads
                    statReadEvents += readableSockets.Count;
                    for (int i = 0; i < readableSockets.Count; i++)
                    {
                        TSocket socket = readableSockets[i];
                        int availableBytes = !checkAvailable ? 0 : socket.Socket.GetAvailableBytes();
                        var receiveResult = socket.Receive(availableBytes);
                        socket.OnReceiveFromSocket(receiveResult);
                    }
                    readableSockets.Clear();

                    // reregister for events
                    for (int i = 0; i < reregisterEventSockets.Count; i++)
                    {
                        var tsocket = reregisterEventSockets[i];
                        lock (tsocket.Gate)
                        {
                            var pendingEventState = tsocket.PendingEventState & ~TSocket.EventControlPending;
                            tsocket.PendingEventState = pendingEventState;
                            UpdateEPollControl(tsocket, pendingEventState, registered: true);
                        }
                    }
                    reregisterEventSockets.Clear();

                    // handle pipe
                    if (pipeReadable)
                    {
                        PosixResult result;
                        do
                        {
                            result = readEnd.TryReadByte();
                            if (result.Value == PipeStopSockets)
                            {
                                StopSockets(threadContext.Sockets);
                            }
                            else if (result.Value == PipeStopThread)
                            {
                                running = false;
                            }
                        } while (result);
                        pipeReadable = false;
                    }

                    // scheduled work
                    threadContext.DoScheduledWork();

                } while (running);

                threadContext.Logger.LogInformation($"Thread {_threadId}: Stats A/AE:{statAccepts}/{statAcceptEvents} RE:{statReadEvents} WE:{statWriteEvents} ZCS/ZCC:{statZeroCopySuccess}/{statZeroCopyCopied}");
            }
            catch (Exception ex)
            {
                Environment.FailFast("TransportThread", ex);
            }
            finally
            {
                // We are not using SafeHandles for epoll to increase performance.
                // running == false when there are no more Sockets
                // so we are sure there are no more epoll users.
                threadContext?.Dispose();

                CompleteStateChange(State.Stopped);
            }
        }

        private static readonly IPAddress NotIPSocket = IPAddress.None;

        private static int HandleAccept(TSocket tacceptSocket, ThreadContext threadContext)
        {
            var type = tacceptSocket.Type;
            Socket clientSocket;
            PosixResult result;
            if (type == SocketFlags.TypeAccept)
            {
                // TODO: should we handle more than 1 accept? If we do, we shouldn't be to eager
                //       as that might give the kernel the impression we have nothing to do
                //       which could interfere with the SO_REUSEPORT load-balancing.
                result = tacceptSocket.Socket.TryAccept(out clientSocket, blocking: false);
            }
            else
            {
                result = tacceptSocket.Socket.TryReceiveSocket(out clientSocket, blocking: false);
                if (result.Value == 0)
                {
                    // The socket passing us file descriptors has closed.
                    // We dispose our end so we get get removed from the epoll.
                    tacceptSocket.Socket.Dispose();
                    return 0;
                }
            }
            if (result.IsSuccess)
            {
                int fd;
                TSocket tsocket;
                try
                {
                    fd = clientSocket.DangerousGetHandle().ToInt32();

                    bool ipSocket = !object.ReferenceEquals(tacceptSocket.LocalAddress, NotIPSocket);

                    // Store the last LocalAddress on the tacceptSocket so we might reuse it instead
                    // of allocating a new one for the same address.
                    IPEndPointStruct localAddress = default(IPEndPointStruct);
                    IPEndPointStruct remoteAddress = default(IPEndPointStruct);
                    if (ipSocket && clientSocket.TryGetLocalIPAddress(out localAddress, tacceptSocket.LocalAddress))
                    {
                        tacceptSocket.LocalAddress = localAddress.Address;
                        clientSocket.TryGetPeerIPAddress(out remoteAddress);
                    }
                    else
                    {
                        // This is not an IP socket.
                        tacceptSocket.LocalAddress = NotIPSocket;
                        ipSocket = false;
                    }

                    tsocket = new TSocket(threadContext, SocketFlags.TypeClient)
                    {
                        Fd = fd,
                        Socket = clientSocket,
                        RemoteAddress = remoteAddress.Address,
                        RemotePort = remoteAddress.Port,
                        LocalAddress = localAddress.Address,
                        LocalPort = localAddress.Port,
                        ZeroCopyThreshold = tacceptSocket.ZeroCopyThreshold
                    };

                    if (ipSocket)
                    {
                        clientSocket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, 1);
                    }
                }
                catch
                {
                    clientSocket.Dispose();
                    return 0;
                }

                threadContext.ConnectionHandler.OnConnection(tsocket);

                var sockets = threadContext.Sockets;
                lock (sockets)
                {
                    sockets.Add(fd, tsocket);
                }

                bool dataMayBeAvailable = (tacceptSocket.Flags & SocketFlags.DeferAccept) != 0;
                tsocket.Start(dataMayBeAvailable);

                return 1;
            }
            else
            {
                return 0;
            }
        }

        internal static readonly Exception EofSentinel = new Exception();
        internal static readonly Exception EAgainSentinel = new Exception();

        private void CloseAccept(ThreadContext threadContext, Dictionary<int, TSocket> sockets)
        {
            var acceptSockets = threadContext.AcceptSockets;
            lock (sockets)
            {
                for (int i = 0; i < acceptSockets.Count; i++)
                {
                    threadContext.RemoveSocket(acceptSockets[i].Fd);
                }
            }
            for (int i = 0; i < acceptSockets.Count; i++)
            {
                // close causes remove from epoll (CLOEXEC)
                acceptSockets[i].Socket.Dispose(); // will close (no concurrent users)
            }
            acceptSockets.Clear();
            CompleteStateChange(State.AcceptClosed);
        }

        private static void StopSockets(Dictionary<int, TSocket> sockets)
        {
            Dictionary<int, TSocket> clone;
            lock (sockets)
            {
                clone = new Dictionary<int, TSocket>(sockets);
            }
            foreach (var kv in clone)
            {
                var tsocket = kv.Value;
                tsocket.Stop();
            }
        }

        // must be called under tsocket.Gate
        private static void UpdateEPollControl(TSocket tsocket, EPollEvents flags, bool registered)
        {
            flags &= EPollEvents.Readable | EPollEvents.Writable | EPollEvents.Error;
            EPollInterop.EPollControl(tsocket.ThreadContext.EPollFd,
                        registered ? EPollOperation.Modify : EPollOperation.Add,
                        tsocket.Fd,
                        flags | EPollEvents.OneShot,
                        EPollData(tsocket.Fd));
        }

        private void ThrowInvalidState()
        {
            throw new InvalidOperationException($"nameof(TransportThread) is {_state}");
        }

        private void CompleteStateChange(State state)
        {
            TaskCompletionSource<object> tcs;
            lock (_gate)
            {
                tcs = _stateChangeCompletion;
                _stateChangeCompletion = null;
                _state = state;
            }
            ThreadPool.QueueUserWorkItem(o =>
            {
                tcs?.SetResult(null);
            });
        }

        private static long EPollData(int fd) => (((long)(uint)fd) << 32) | (long)(uint)fd;

        internal static MemoryPool CreateMemoryPool()
        {
            return new MemoryPool();
        }
    }
}
