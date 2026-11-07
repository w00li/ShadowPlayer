using M3uPlayer.Core.Localization;
using M3uPlayer.App.Player;
using M3uPlayer.Core.Catchup;
using M3uPlayer.Core.Media;
using M3uPlayer.Core.Storage;

namespace M3uPlayer.App.ViewModels;

public sealed partial class LiveViewModel
{
    // ---- archive ----
    public void GoLive()
    {
        _archiveResumable = false;
        Archive.Clear();
        if (SelectedChannel is { } row) StartPlayback(row);
        Raise();
        UpdateOverlay();
    }

    /// <summary>Plays a programme from its start, or from <paramref name="at"/>. Tells the viewer when the archive does not reach it.</summary>
    public void PlayProgramme(GuideItem item, TimeSpan at = default)
    {
        if (SelectedChannel is null || item.Programme is not { } p) return;
        if (!item.CanReplay) { Guide.ShowNotice(NoArchiveReach); return; }
        // The guide is already corrected by tvg-shift to real broadcast time, so the shifted start is what the provider's archive expects.
        var timeline = new ArchiveTimeline(p.Start, p.Stop, at);
        if (!TryOpen(timeline, at))
        {
            Guide.ShowNotice(NoArchiveAddress);
            return;
        }
        Archive.Begin(timeline, p.Title, _time.GetUtcNow());
        Raise();
        UpdateOverlay();
    }

    /// <summary>The programme ran to its end: on to the next one, or live when the archive has nothing further yet.</summary>
    private void PlayNextProgramme()
    {
        var now = _time.GetUtcNow();
        Guide.SetTime(now);
        var next = SelectedChannel is { } row && Archive.Timeline is { } t
            ? ArchiveTimeline.NextProgramme(Guide.Items.Select(i => i.Programme).OfType<Programme>(), t.Start, row.Channel.Catchup, now)
            : null;
        if (next is null || Guide.Items.FirstOrDefault(i => ReferenceEquals(i.Programme, next)) is not { } item)
        {
            GoLive();
            return;
        }
        Guide.Follow(item);
        PlayProgramme(item);
    }

    private static string NoArchiveReach => Strings.Guide_NoArchive;
    private static string NoArchiveAddress => Strings.Guide_NoArchiveAddress;

    /// <summary>Opens the programme at the offset. False when the provider's scheme gives no address.</summary>
    private bool TryOpen(ArchiveTimeline timeline, TimeSpan offset)
    {
        if (SelectedChannel?.Channel is not { } c) return false;
        var url = CatchupUrlBuilder.Build(c.Catchup, c.Url, timeline.Start + offset, timeline.Stop, _time.GetUtcNow());
        if (url is null) return false;
        _spinnerArmed = false;
        _archiveResumable = _ignorePositions = false;
        _player.Play(new PlayRequest(url, c.UserAgent, c.Referrer, false));
        return true;
    }

    private void OpenArchive(TimeSpan offset)
    {
        if (Archive.Timeline is not { } t) return;
        if (!TryOpen(t, offset)) { Guide.ShowNotice(NoArchiveAddress); return; }
        Archive.Reopened(offset, _time.GetUtcNow());
        UpdateOverlay();
    }

    public void SeekTo(double seconds)
    {
        if (Archive.Timeline is not { } t) return;
        var now = _time.GetUtcNow();
        var plan = t.SeekTo(TimeSpan.FromSeconds(seconds), now, _player.IsSeekable && IsActive);
        if (plan.Reopen) OpenArchive(plan.Offset);
        else
        {
            _player.Seek(plan.PlayerPosition);
            Archive.Seeked(plan.PlayerPosition, now);
        }
    }

    /// <summary>In the archive: a seek. Live, going back: the current programme from the archive, a step behind its furthest point.</summary>
    public void SeekBy(TimeSpan delta)
    {
        if (Archive.Active) SeekTo((Archive.Elapsed + delta).TotalSeconds);
        else if (delta < TimeSpan.Zero && CurrentLiveProgramme is { Programme: { } p } item)
            PlayProgramme(item, new ArchiveTimeline(p.Start, p.Stop).BehindLive(_time.GetUtcNow(), -delta));
    }

    /// <summary>The start of the programme: the archived one being watched, or the one on air now.</summary>
    public void RestartProgramme()
    {
        if (Archive.Active) SeekTo(0);
        else if (CurrentLiveProgramme is { } item) PlayProgramme(item);
    }

    public bool CanRestart => SelectedChannel is not null && (Archive.Active || CurrentLiveProgramme is not null);

    private GuideItem? CurrentLiveProgramme => Guide.Items.FirstOrDefault(i => i.IsCurrent && i.Programme is not null && i.CanReplay);

    private void OnPlayerPosition(TimeSpan p) => _dq.TryEnqueue(() =>
    {
        // stopping reports 0, which must not replace the saved place
        if (!_disposed && !_ignorePositions && !_archiveResumable && Archive.SetPosition(p, _time.GetUtcNow())) PlayNextProgramme();
    });

    private bool _resume, _archiveResumable, _ignorePositions;

    /// <summary>The page really leaves (navigation): the video surface goes away, so playback stops; it resumes when the page comes back.</summary>
    public void LeaveScreen()
    {
        _tickTimer.Stop();
        _ignorePositions = true; // set first: the stop below reports position 0 on the queue, behind this call
        _resume = IsActive && _state != PlayerState.Paused;
        _archiveResumable = Archive.Active && _state == PlayerState.Paused; // a paused archive stays where it was; the overlay offers to continue
        FlushVolume();
        _player.Stop();
    }

    /// <summary>The page is shown again; the "no autoplay" rule is only for app start.</summary>
    public void ReturnToScreen()
    {
        if (!_resume) return;
        _resume = false;
        if (Archive.Active) OpenArchive(Archive.Elapsed);
        else if (SelectedChannel is { } row) StartPlayback(row);
    }
}
