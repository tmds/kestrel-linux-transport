using System;

namespace RedHatX.AspNetCore.Server.Kestrel.Transport.Linux
{
    sealed partial class TransportThread
    {
        sealed class SocketDictionary
        {
            private int _count;
            private TSocket[] _sockets;
            private int _previous;

            public SocketDictionary()
            {
                _previous = -1;
                _sockets = new TSocket[4];
                _count = 0;
            }

            public int Add(TSocket socket)
            {
                int capacity = _sockets.Length;
                // Resize when using using 3/4 capacity
                if (_count >= ((capacity >> 1) + (capacity >> 2)))
                {
                    capacity = capacity << 1;
                    Resize(capacity);
                }

                int key = FindKey();                
                _sockets[key] = socket;
                _count++;
                return key;
            }

            private void Resize(int newCapacity)
            {
                var newSockets = new TSocket[newCapacity];
                Array.Copy(_sockets, newSockets, _sockets.Length);
                _sockets = newSockets;
            }

            private int FindKey()
            {
                int i = (_previous + 1) % _sockets.Length;
                for (;i < _sockets.Length; i++)
                {
                    if (_sockets[i] == null)
                    {
                        _previous = i;
                        return i;
                    }
                }
                for (i = 0; i <= _previous && _sockets[i] != null; i++)
                {
                    if (_sockets[i] == null)
                    {
                        _previous = i;
                        return i;
                    }
                }
                return -1;
            }

            public void Remove(int key)
            {
                if (_sockets[key] != null)
                {
                    _count--;
                    _sockets[key] = null;
                }
            }

            public TSocket this[int key] => _sockets[key];

            public TSocket[] CloneSocketArray() => (TSocket[])_sockets.Clone();

            public int Count => _count;
        }
    }
}