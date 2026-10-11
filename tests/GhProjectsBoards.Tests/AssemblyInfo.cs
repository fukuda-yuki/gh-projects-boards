using NUnit.Framework;

// Bound fake-gh subprocess and storage contention even on machines with many cores.
[assembly: LevelOfParallelism(4)]
