#if !WINDOWS
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using WinUI.TableView.Extensions;

namespace WinUI.TableView;

/// <summary>
/// Partial class for TableView that contains Uno stuff.
/// </summary>
partial class TableView
{
    private const BindingFlags BindingAttr = BindingFlags.NonPublic | BindingFlags.Instance;
    private PropertyInfo? _disableRaiseSelectionChangedPropertyInfo;
    private MethodInfo? _invokeSelectionChangedMethodInfo;

    private void SetDisableRaiseSelectionChanged(bool value)
    {
        _disableRaiseSelectionChangedPropertyInfo ??= typeof(Selector).GetProperty("DisableRaiseSelectionChanged", BindingAttr);
        _disableRaiseSelectionChangedPropertyInfo?.SetValue(this, value);
    }

    private void InvokeSelectionChanged(object[] removedItems, object[] addedItems)
    {
        _invokeSelectionChangedMethodInfo ??= typeof(Selector).GetMethod("InvokeSelectionChanged", BindingAttr);
        _invokeSelectionChangedMethodInfo?.Invoke(this, [removedItems, addedItems]);
    }

    private new void DeselectRange(ItemIndexRange itemIndexRange)
    {
        var removedItems = new List<object>();

        SetDisableRaiseSelectionChanged(true);
        {
            if (!itemIndexRange.IsValid(this))
            {
                throw new IndexOutOfRangeException("The given item index range bounds are not valid.");
            }

            for (var index = itemIndexRange.FirstIndex; index <= itemIndexRange.LastIndex; index++)
            {
                var item = Items[index];
                if (SelectedItems.Contains(item))
                {
                    removedItems.Add(item);
                    SelectedItems.Remove(item);
                }
            }

            AdjustSelectedRanges();
        }
        SetDisableRaiseSelectionChanged(false);

        InvokeSelectionChanged([.. removedItems], []);
    }

    private void AdjustSelectedRanges()
    {
        SelectedRanges.Clear();

        if (SelectedItems.Count == 0) return;

        // FIX E: a selected leaf whose page was LRU-evicted resolves to IndexOf == -1; folding
        // those in yields bogus ranges anchored at -1. Drop negatives before building ranges,
        // and bail if nothing resolvable remains.
        var selectedIndexes = SelectedItems.Select(Items.IndexOf).Where(i => i >= 0).Order().ToList();
        if (selectedIndexes.Count == 0) return;

        var start = selectedIndexes[0];
        var prev = start;

        foreach (var index in selectedIndexes)
        {
            if (index != prev + 1)
            {
                var length = (uint)(prev - start + 1);
                SelectedRanges.Add(new ItemIndexRange(start, length));
                start = index;
            }
            prev = index;
        }

        var finalLength = (uint)(prev - start + 1);
        SelectedRanges.Add(new ItemIndexRange(start, finalLength));
    }

    private new void SelectRange(ItemIndexRange itemIndexRange)
    {
        var addedItems = new List<object>();

        SetDisableRaiseSelectionChanged(true);
        {
            if (!itemIndexRange.IsValid(this))
            {
                throw new IndexOutOfRangeException("The given item index range bounds are not valid.");
            }

            for (var index = itemIndexRange.FirstIndex; index <= itemIndexRange.LastIndex; index++)
            {
                var item = Items[index];
                if (!SelectedItems.Contains(item))
                {
                    addedItems.Add(item);
                    SelectedItems.Add(item);
                }
            }

            AdjustSelectedRanges();
        }
        SetDisableRaiseSelectionChanged(false);

        InvokeSelectionChanged([], [.. addedItems]);
    }

    private new IList<ItemIndexRange> SelectedRanges { get; } = [];

    /// <summary>
    /// Repaints the rows after a tree row is expanded/collapsed. Uno-Skia's virtualizing
    /// <c>ItemsStackPanel</c> does not re-arrange its realized containers when items are
    /// removed from the bound collection (as <see cref="TreeGridFlattener{T}"/> does on
    /// collapse), leaving a stale empty band above the rows until the user scrolls. Force a
    /// re-measure and then reproduce that scroll programmatically (a 1px hop and back) so the
    /// panel re-realizes immediately. Called from the tree column's chevron.
    /// </summary>
    public async void RefreshAfterTreeToggle()
    {
        ItemsPanelRoot?.InvalidateMeasure();

        if (_scrollViewer is not { } sv)
            return;

        var target = sv.VerticalOffset;
        if (target <= 0)
            return;

        // Staged late double-nudges: the panel's post-rebind rebuild finishes asynchronously,
        // and only a view change AFTER it lands re-anchors realization to the offset. Early
        // nudges get wiped, so repeat at increasing delays; two DISTINCT offsets per nudge
        // because a same-offset ChangeView is a no-op. Runs from the chevron's event context —
        // timers created inside the rebind window itself provably stop firing.
        foreach (var delayMs in new[] { 250, 800, 2000 })
        {
            await Task.Delay(delayMs);
            if (_scrollViewer is not { } s || Math.Abs(s.VerticalOffset - target) > 300)
                return;   // grid gone or the user scrolled away — their position wins

            s.ChangeView(null, Math.Max(0, target - 1), null, disableAnimation: true);
            await Task.Delay(60);
            if (_scrollViewer is not { } s2)
                return;
            s2.ChangeView(null, target, null, disableAnimation: true);
        }
    }
}
#endif