using System.Collections.Generic;

namespace SteamTokenDumper
{
    internal static class Extensions
    {
        // https://codereview.stackexchange.com/a/90531
        public static IEnumerable<List<T>> Split<T>(this IEnumerable<T> fullBatch, int chunkSize)
        {
            var cellCounter = 0;
            var chunk = new List<T>(chunkSize);

            foreach (var element in fullBatch)
            {
                if (cellCounter++ == chunkSize)
                {
                    yield return chunk;
                    chunk = new List<T>(chunkSize);
                    cellCounter = 1;
                }

                chunk.Add(element);
            }

            yield return chunk;
        }
    }
}
