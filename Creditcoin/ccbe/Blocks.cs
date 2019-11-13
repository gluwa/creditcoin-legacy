using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace ccbe
{
    internal class Blocks : IReadOnlyDictionary<string, Models.Block>
    {
        private List<KeyValuePair<string, Models.Block>> blocks;

        public Blocks(List<KeyValuePair<string, Models.Block>> blocks)
        {
            this.blocks = blocks;
        }

        public Models.Block this[string key] => blocks.SingleOrDefault(e => e.Key.Equals(key)).Value;

        public KeyValuePair<string, Models.Block> this[int index] => blocks[index];

        public IEnumerable<string> Keys => blocks.Select(e => e.Key);

        public IEnumerable<Models.Block> Values => blocks.Select(e => e.Value);

        public int Count => blocks.Count;

        public bool ContainsKey(string key)
        {
            return this[key] != null;
        }

        public IEnumerator<KeyValuePair<string, Models.Block>> GetEnumerator()
        {
            return blocks.GetEnumerator();
        }

        public bool TryGetValue(string key, out Models.Block value)
        {
            value = this[key];
            return value != null;
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return blocks.GetEnumerator();
        }
    }
}
