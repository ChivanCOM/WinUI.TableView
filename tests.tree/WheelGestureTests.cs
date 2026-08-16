using WinUI.TableView;

namespace WinUI.TableView.TreeTests;

/// <summary>
/// A wheel and a flick both arrive as wheel deltas, and only one of them wants pacing: pacing a
/// wheel is a list that carries on moving after the hand has stopped. What separates them is the
/// shape of the deltas and how fast they come, which is arithmetic, so it is tested here rather than
/// left inside a pointer handler where it cannot be.
/// </summary>
public class WheelGestureTests
{
    private const int Notch = WheelGesture.DetentUnits;

    [Fact]
    public void A_wheel_turned_by_hand_is_never_paced()
    {
        var gesture = new WheelGesture();

        // Opening click, then a hand's worth of turning: whole notches, tens of milliseconds apart.
        Assert.False(gesture.Observe(Notch, double.PositiveInfinity));
        Assert.False(gesture.Observe(Notch, 40));
        Assert.False(gesture.Observe(Notch, 33));
        Assert.False(gesture.Observe(-Notch, 55));

        // A fast turn coalesces clicks into one event; still a wheel.
        Assert.False(gesture.Observe(Notch * 3, 20));
    }

    [Fact]
    public void A_delta_that_is_not_a_whole_notch_is_a_flick()
    {
        var gesture = new WheelGesture();

        Assert.True(gesture.Observe(17, double.PositiveInfinity));
    }

    [Fact]
    public void Notch_shaped_deltas_arriving_faster_than_a_hand_are_a_flick()
    {
        var gesture = new WheelGesture();

        // The case that matters where a platform quantizes its trackpad to notches: the shape says
        // wheel, the rate says otherwise. The opening event is judged innocent; the density that
        // follows is not.
        Assert.False(gesture.Observe(Notch, double.PositiveInfinity));
        Assert.True(gesture.Observe(Notch, 4));
    }

    [Fact]
    public void A_gesture_that_has_shown_itself_a_flick_stays_one()
    {
        var gesture = new WheelGesture();

        Assert.True(gesture.Observe(23, double.PositiveInfinity));

        // Momentum thins out towards the end, and some of what it sends lands on a whole notch by
        // arithmetic accident. Flipping back to unpaced mid-gesture reads worse than either mode.
        Assert.True(gesture.Observe(Notch, 60));
        Assert.True(gesture.Observe(Notch, 90));
    }

    [Fact]
    public void A_quiet_moment_starts_the_judging_again()
    {
        var gesture = new WheelGesture();

        Assert.True(gesture.Observe(23, double.PositiveInfinity));
        Assert.True(gesture.IsFlick);

        // Fingers lifted, then a click of the wheel. That is a new gesture and it is not a flick.
        Assert.False(gesture.Observe(Notch, WheelGesture.GestureGapMs + 1));
        Assert.False(gesture.IsFlick);
    }

    [Fact]
    public void A_zero_delta_is_not_treated_as_a_wheel()
    {
        var gesture = new WheelGesture();

        // Nothing with a ratchet in it reports no movement; whatever this is, it is not a hand.
        Assert.True(gesture.Observe(0, double.PositiveInfinity));
    }
}
