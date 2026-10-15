namespace GhProjectsBoards.Core.PlanEditor;

internal static class PlanOrder
{
    // Keep the LIS with lexicographically earliest current-plan indices. Membership
    // changes do not move surviving rows. O(n log n) time, O(n) space.
    internal static HashSet<string> Moved(IEnumerable<string> baseline, IEnumerable<string> current)
    {
        var positions = baseline.Select((id, index) => (id, index)).ToDictionary(p => p.id, p => p.index);
        var shared = current.Where(positions.ContainsKey).ToArray();
        var lengths = new int[shared.Length];
        var tails = new List<int>();
        // Reversing both traversal and values gives the LIS length starting at each row.
        for (var i = shared.Length - 1; i >= 0; i--)
        {
            var value = -positions[shared[i]];
            var place = tails.BinarySearch(value);
            if (place < 0) place = ~place;
            if (place == tails.Count) tails.Add(value); else tails[place] = value;
            lengths[i] = place + 1;
        }
        var remaining = tails.Count;
        var previous = -1;
        var moved = new HashSet<string>();
        for (var i = 0; i < shared.Length; i++)
        {
            var position = positions[shared[i]];
            if (position > previous && lengths[i] >= remaining && remaining > 0)
            {
                previous = position;
                remaining--;
            }
            else moved.Add(shared[i]);
        }
        return moved;
    }
}
