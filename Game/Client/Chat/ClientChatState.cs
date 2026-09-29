using System;
using System.Collections.Generic;
using Game.Shared.Chat;

namespace Game.Client.Chat
{
    /// <summary>
    /// Client-only bounded presentation state. It is fed by pushed chat deliveries;
    /// it never polls gameplay or server state.
    /// </summary>
    public sealed class ClientChatState
    {
        public const int DefaultCapacity = 80;

        public readonly struct Entry
        {
            public readonly ChatChannel Channel;
            public readonly string Sender;
            public readonly string Target;
            public readonly string Message;
            public readonly long ServerUtcTicks;

            public Entry(ClientChatMessage source)
            {
                Channel = source.Channel;
                Sender = source.Sender ?? string.Empty;
                Target = source.Target ?? string.Empty;
                Message = source.Message ?? string.Empty;
                ServerUtcTicks = source.ServerUtcTicks;
            }
        }

        private readonly int _capacity;
        private readonly Queue<Entry> _entries;

        public event Action Changed;

        public ClientChatState(int capacity = DefaultCapacity)
        {
            _capacity = Math.Max(10, capacity);
            _entries = new Queue<Entry>(_capacity);
        }

        public IEnumerable<Entry> Entries => _entries;

        public void Add(ClientChatMessage message)
        {
            while (_entries.Count >= _capacity)
                _entries.Dequeue();
            _entries.Enqueue(new Entry(message));
            Changed?.Invoke();
        }

        public void AddSystem(string message)
        {
            Add(new ClientChatMessage(
                ChatChannel.System,
                string.Empty,
                string.Empty,
                message ?? string.Empty,
                DateTime.UtcNow.Ticks));
        }

        public void Clear()
        {
            if (_entries.Count == 0)
                return;
            _entries.Clear();
            Changed?.Invoke();
        }
    }
}
