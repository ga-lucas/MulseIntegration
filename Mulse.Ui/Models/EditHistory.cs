namespace Mulse.Ui.Models;

/// <summary>
/// A bounded undo/redo history over immutable snapshots, with a "saved" baseline so an editor can tell whether it has
/// unsaved changes and revert to what was last loaded or saved. It holds no UI state and performs no I/O.
/// </summary>
/// <remarks>
/// <see cref="Current"/> is the snapshot being edited. Recording a change pushes the previous snapshot onto the undo
/// stack and clears redo. Rapid edits that share a coalesce key (typing in one field) replace the latest entry instead
/// of adding one each, so one undo reverses the whole burst. The history is bounded by entry count and by an optional
/// total size so a very large draft cannot grow it without limit; the oldest entries are dropped first.
/// </remarks>
public sealed class EditHistory<T> where T : notnull
{
    public const int DefaultMaxEntries = 100;
    public static readonly TimeSpan DefaultCoalesceWindow = TimeSpan.FromSeconds(1);

    private readonly List<T> _undo = [];
    private readonly List<T> _redo = [];
    private readonly IEqualityComparer<T> _comparer;
    private readonly Func<T, long>? _measure;
    private readonly long _maxSize;
    private readonly int _maxEntries;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _coalesceWindow;
    private string? _lastKey;
    private DateTimeOffset _lastAt;

    public EditHistory(
        T initial,
        int maxEntries = DefaultMaxEntries,
        IEqualityComparer<T>? comparer = null,
        Func<T, long>? measure = null,
        long maxSize = 0,
        TimeProvider? clock = null,
        TimeSpan? coalesceWindow = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        _maxEntries = maxEntries;
        _comparer = comparer ?? EqualityComparer<T>.Default;
        _measure = measure;
        _maxSize = maxSize;
        _clock = clock ?? TimeProvider.System;
        _coalesceWindow = coalesceWindow ?? DefaultCoalesceWindow;
        Current = Baseline = initial;
    }

    /// <summary>The snapshot being edited.</summary>
    public T Current { get; private set; }

    /// <summary>The snapshot last loaded or saved; "unsaved changes" means <see cref="Current"/> differs from it.</summary>
    public T Baseline { get; private set; }

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public bool IsDirty => !_comparer.Equals(Current, Baseline);

    /// <summary>
    /// Records <paramref name="state"/> as the new current snapshot. Returns false (and changes nothing) when it equals the
    /// current one. A non-null <paramref name="coalesceKey"/> that matches the previous recording made within the
    /// coalesce window replaces the latest entry rather than adding another.
    /// </summary>
    public bool Record(T state, string? coalesceKey = null)
    {
        if (_comparer.Equals(Current, state))
        {
            return false;
        }

        var now = _clock.GetUtcNow();
        var coalesce = coalesceKey is not null
            && string.Equals(coalesceKey, _lastKey, StringComparison.Ordinal)
            && now - _lastAt <= _coalesceWindow
            && _redo.Count == 0
            && _undo.Count > 0;
        if (!coalesce)
        {
            _undo.Add(Current);
        }

        _redo.Clear();
        Current = state;
        _lastKey = coalesceKey;
        _lastAt = now;
        Trim();
        return true;
    }

    public bool Undo(out T state)
    {
        if (_undo.Count == 0)
        {
            state = Current;
            return false;
        }

        _redo.Add(Current);
        Current = state = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _lastKey = null;
        return true;
    }

    public bool Redo(out T state)
    {
        if (_redo.Count == 0)
        {
            state = Current;
            return false;
        }

        _undo.Add(Current);
        Current = state = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _lastKey = null;
        Trim();
        return true;
    }

    /// <summary>Makes the baseline the current snapshot as an ordinary, undoable edit. Returns false when already there.</summary>
    public bool RevertToBaseline(out T state)
    {
        state = Baseline;
        return Record(Baseline);
    }

    /// <summary>Marks the current snapshot as the saved one, keeping undo and redo history.</summary>
    public void MarkSaved() => Baseline = Current;

    /// <summary>Starts over from <paramref name="state"/>: clears undo and redo and makes it both current and baseline.</summary>
    public void Reinitialize(T state)
    {
        _undo.Clear();
        _redo.Clear();
        Current = Baseline = state;
        _lastKey = null;
    }

    private void Trim()
    {
        while (_undo.Count > _maxEntries)
        {
            _undo.RemoveAt(0);
        }

        if (_measure is null || _maxSize <= 0)
        {
            return;
        }

        long total = 0;
        foreach (var entry in _undo) total += _measure(entry);
        foreach (var entry in _redo) total += _measure(entry);
        while (total > _maxSize && _undo.Count > 1)
        {
            total -= _measure(_undo[0]);
            _undo.RemoveAt(0);
        }
    }
}
