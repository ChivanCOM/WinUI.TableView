// FOBO fork addition.
//
// Telling a wheel from a flick, from the deltas alone — kept apart from the handler that acts on it
// so it can be tested without a pointer, the same split ColumnStrip gets.
//
// The list is tempered because a trackpad flick arrives as a few hundred tiny scrolls and each one
// makes the layouter walk (see TableView.OnScrollContentPresenterPointerWheelChanged). A mouse wheel
// is not that: it arrives a few dozen times a second at most, a whole detent at a time, and it wants
// the scroll the platform would have given it — tempering one is a list that carries on moving after
// the hand has stopped, because the backlog is still draining.

using System;

namespace WinUI.TableView;

/// <summary>
/// Which kind of scrolling is happening right now: a mouse wheel, or a flick that has to be paced.
///
/// <para>Two things separate them, and either is enough. A wheel turns in DETENTS, so its deltas are
/// whole multiples of one notch; a trackpad measures fingers and reports whatever it measured. And a
/// wheel is a hand, so its events are tens of milliseconds apart; a flick's are a frame or less
/// apart.</para>
///
/// <para>A gesture is judged as a whole, not event by event. Once anything in it looks like a flick
/// the rest of it is a flick, because the first event or two of a flick can look like anything —
/// otherwise a gesture would flip between paced and free while it ran, which reads worse than either
/// on its own. A quiet moment ends the gesture and the next one starts over as a wheel: a scroll
/// that arrives on its own, after a pause, is a click of a wheel however large it is.</para>
/// </summary>
public sealed class WheelGesture
{
    /// <summary>One notch of a wheel, as the platform reports it. A wheel reports this or a whole
    /// multiple of it (a fast turn coalesces two clicks into one event); anything else was measured
    /// off a surface rather than counted off a ratchet.</summary>
    public const int DetentUnits = 120;

    /// <summary>Nothing for this long and the gesture is over — the next event starts a new one,
    /// judged from scratch. Longer than the gap between two clicks of a wheel turned slowly, and far
    /// longer than anything inside a flick.</summary>
    public const double GestureGapMs = 150;

    /// <summary>Events closer together than this are not a hand on a ratchet. A wheel spun hard
    /// still leaves more than a frame between clicks; a flick fills every frame and then some.</summary>
    public const double DenseGapMs = 10;

    private bool _isFlick;

    /// <summary>What the gesture in progress is, without asking anything of it. False before the
    /// first event of a session and between gestures.</summary>
    public bool IsFlick => _isFlick;

    /// <summary>
    /// Takes one wheel event and says whether the gesture it belongs to should be paced.
    /// </summary>
    /// <param name="delta">The event's wheel delta, in the platform's own units. Sign is ignored.</param>
    /// <param name="sinceLastMs">
    /// How long since the previous wheel event. The first event of a session should pass
    /// <see cref="double.PositiveInfinity"/> (or anything above <see cref="GestureGapMs"/>).
    /// </param>
    public bool Observe(int delta, double sinceLastMs)
    {
        // A quiet moment: whatever was running has finished, and this is the opening event of
        // something new. Innocent until proven otherwise — a lone scroll is a wheel.
        if (sinceLastMs >= GestureGapMs)
        {
            _isFlick = false;
        }

        if (_isFlick)
        {
            return true;
        }

        var size = Math.Abs(delta);

        // Not a whole number of notches: nothing with a ratchet in it produced this.
        if (size == 0 || size % DetentUnits != 0)
        {
            _isFlick = true;
        }
        // Detent-shaped, but arriving faster than a hand can turn a wheel. This is the case that
        // matters where the platform quantizes a trackpad's deltas to notches anyway, which is
        // exactly where the shape test above says "wheel" about a flick.
        else if (sinceLastMs < DenseGapMs)
        {
            _isFlick = true;
        }

        return _isFlick;
    }

    /// <summary>Forgets the gesture in progress, so the next event is judged as an opening one.</summary>
    public void Reset() => _isFlick = false;
}
