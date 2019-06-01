using System;
using System.Collections.Generic;

namespace SteamTokenDumper
{
    internal static class Extensions
    {
        // https://codereview.stackexchange.com/a/90531
        public static IEnumerable<IEnumerable<T>> Split<T>(this IEnumerable<T> fullBatch, int chunkSize)
        {
            if (chunkSize <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    "chunkSize",
                    chunkSize,
                    "Chunk size cannot be less than or equal to zero.");
            }

            if (fullBatch == null)
            {
                throw new ArgumentNullException("fullBatch", "Input to be split cannot be null.");
            }

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
