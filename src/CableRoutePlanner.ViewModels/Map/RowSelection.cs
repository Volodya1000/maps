using System.Collections.ObjectModel;

namespace CableRoutePlanner.ViewModels;

internal static class RowSelection
{
    public static T? Apply<T, TKey>(ObservableCollection<T> rows, T? requested,
        Func<T, TKey> key, Func<T, bool> isSelected, Func<T, bool, T> select) where T : class
    {
        T? selected = null;
        for (int index = 0; index < rows.Count; index++)
        {
            T row = rows[index];
            bool matches = requested is not null && EqualityComparer<TKey>.Default.Equals(key(row), key(requested));
            if (isSelected(row) != matches)
            {
                rows[index] = row = select(row, matches);
            }

            if (matches)
            {
                selected = row;
            }
        }

        return selected;
    }
}
