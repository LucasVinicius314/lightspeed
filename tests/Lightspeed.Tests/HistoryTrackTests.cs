using System.Numerics;
using Lightspeed.History;

namespace Lightspeed.Tests;

public class HistoryTrackTests
{
    private const double Dt = 1.0 / 60.0;
    private const float C = 5f;

    private static Sample At(float x) => new(new Vector3(x, 0, 0), Quaternion.Identity);

    [Fact]
    public void Ring_keeps_the_newest_samples_in_order_after_wrapping()
    {
        var track = new HistoryTrack(capacity: 4, row: 0);
        for (int t = 100; t < 110; t++)
            track.Record(t, At(t));

        Assert.Equal(4, track.Count);
        Assert.Equal(100, track.BirthTick);
        Assert.Equal(109, track.LastTick);
        Assert.Equal(106f, track[0].Position.X);
        Assert.Equal(109f, track[3].Position.X);
        Assert.Equal(109f, track.Newest.Position.X);
    }

    [Fact]
    public void Skipped_ticks_are_filled_with_the_previous_pose()
    {
        var track = new HistoryTrack(8, 0);
        track.Record(10, At(1));
        track.Record(13, At(4));

        Assert.Equal(4, track.Count);
        Assert.Equal(new[] { 1f, 1f, 1f, 4f }, new[] { track[0], track[1], track[2], track[3] }.Select(s => s.Position.X));
    }

    [Fact]
    public void Teleport_flag_is_kept_on_that_sample_only()
    {
        var track = new HistoryTrack(8, 0);
        track.Record(1, At(0));
        track.Record(2, At(1));
        track.Record(3, At(50), isBreak: true);
        track.Record(4, At(51));

        Assert.False(track.BreaksBefore(1));
        Assert.True(track.BreaksBefore(2));
        Assert.False(track.BreaksBefore(3));
        Assert.True(track.NewestIsBreak == false);
    }

    [Fact]
    public void Retired_track_stops_recording_and_expires_after_the_window()
    {
        var track = new HistoryTrack(capacity: 100, row: 3);
        track.Record(10, At(0));
        track.Record(11, At(0));
        track.Retire();
        track.Record(12, At(9)); // ignored

        Assert.True(track.IsFinished);
        Assert.Equal(11, track.LastTick);
        Assert.Equal(2, track.Count);
        Assert.False(track.IsExpired(11 + 99));
        Assert.True(track.IsExpired(11 + 100));
    }

    [Fact]
    public void Track_that_never_recorded_expires_immediately_once_retired()
    {
        var track = new HistoryTrack(10, 0);
        Assert.False(track.IsExpired(0));
        track.Retire();
        Assert.True(track.IsExpired(0));
    }

    [Fact]
    public void Despawned_object_stays_visible_until_its_last_light_passes()
    {
        // Stationary object 10 m away, c = 5: its last light reaches the camera 2 s after despawn.
        var track = new HistoryTrack(1000, 0);
        for (int t = 0; t < 300; t++)
            track.Record(t, At(10));
        track.Retire();

        RetardedPose SolveAfter(double secondsSinceLast) =>
            RetardedSolver.Solve(track, Dt, Vector3.Zero, C, Vector3.Zero, secondsSinceLast);

        Assert.Equal(SolveStatus.Ok, SolveAfter(1.0).Status);
        Assert.Equal(SolveStatus.Ok, SolveAfter(1.9).Status);
        Assert.Equal(SolveStatus.NotVisible, SolveAfter(2.1).Status);
    }

    [Fact]
    public void Teleport_leaves_a_gap_between_the_old_and_new_place()
    {
        // 100 samples at 10 m, then a jump to 30 m. c = 5: the old place's last light arrives 2 s after the
        // jump, the new place's first light 6 s after it.
        RetardedPose SolveAfter(int secondsAfterJump)
        {
            var track = new HistoryTrack(2000, 0);
            for (int t = 0; t < 100; t++) track.Record(t, At(10));
            for (int t = 100; t < 100 + secondsAfterJump * 60; t++) track.Record(t, At(30), isBreak: t == 100);
            return RetardedSolver.Solve(track, Dt, Vector3.Zero, C, Vector3.Zero);
        }

        // Before the old light has all arrived: still the old place.
        var early = SolveAfter(1);
        Assert.Equal(SolveStatus.Ok, early.Status);
        Assert.Equal(10f, early.Position.X, 1e-3);

        // In the gap: nothing.
        Assert.Equal(SolveStatus.NotVisible, SolveAfter(4).Status);

        // After the new place's first light arrives: the new place.
        var late = SolveAfter(8);
        Assert.Equal(SolveStatus.Ok, late.Status);
        Assert.Equal(30f, late.Position.X, 1e-3);
    }
}
