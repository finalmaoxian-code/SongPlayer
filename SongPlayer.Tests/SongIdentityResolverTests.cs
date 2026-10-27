using SongPlayer.Models;
using SongPlayer.Services.Lyrics;

namespace SongPlayer.Tests;

/// <summary>
/// Tests for turning a track into something the lyric database can be asked about.
/// </summary>
/// <remarks>
/// The important asymmetry: a local file may carry real tags, while an online video carries a video
/// title and an uploader. Treating the uploader as the artist would make every re-upload channel look
/// like a different performer and would send a wrong exact-match query, so the resolver must keep that
/// distinction and must never invent what it does not know.
/// </remarks>
public sealed class SongIdentityResolverTests
{
    private static readonly SongIdentityResolver Resolver = new();

    private static Track Stream(string title, string uploader, TimeSpan? duration = null, string? clue = null) =>
        new(
            Title: title,
            Artist: uploader,
            Duration: duration ?? TimeSpan.FromSeconds(239),
            StreamKey: "bilibili:BV1xx411c7mD",
            StreamUri: "https://example.invalid/audio.m4a",
            SearchClue: clue);

    private static Track Local(string title, string artist, string? album = null, string? path = null) =>
        new(
            Title: title,
            Artist: artist,
            Duration: TimeSpan.FromSeconds(239),
            FilePath: path ?? @"D:\Music\Creep.flac",
            Album: album);

    [Fact]
    public void A_video_uploader_is_never_used_as_the_artist()
    {
        SongIdentity identity = Resolver.Resolve(Stream("Creep", "Some Upload Channel"));

        Assert.True(identity.HasSongTitle);
        Assert.False(identity.HasArtist);
        Assert.Equal("Creep", identity.SongTitle);
    }

    [Fact]
    public void A_video_title_loses_display_noise_but_keeps_the_version()
    {
        SongIdentity clean = Resolver.Resolve(Stream("Creep (Official MV) [HD]", "uploader"));
        SongIdentity live = Resolver.Resolve(Stream("Creep (Live in Paris) 现场版", "uploader"));

        Assert.Equal("Creep", clean.SongTitle);
        Assert.Equal(SongVersion.Unknown, clean.Version);
        Assert.Equal(SongVersion.Live, live.Version);
    }

    [Fact]
    public void A_search_clue_supplies_an_artist_only_when_it_names_the_same_song()
    {
        SongIdentity agreeing = Resolver.Resolve(Stream("Radiohead - Creep (Official MV)", "uploader"), "Radiohead - Creep");
        SongIdentity disagreeing = Resolver.Resolve(Stream("Creep", "uploader"), "Oasis - Wonderwall");

        Assert.Equal("Radiohead", agreeing.Artist);
        Assert.Null(disagreeing.Artist);
    }

    [Fact]
    public void A_title_that_is_only_a_song_name_gains_no_artist_from_the_clue()
    {
        SongIdentity identity = Resolver.Resolve(Stream("Creep", "uploader"), "Creep");

        Assert.False(identity.HasArtist);
        Assert.Equal("Creep", identity.SongTitle);
    }

    [Fact]
    public void Local_tags_are_trusted_metadata()
    {
        SongIdentity identity = Resolver.Resolve(Local("Creep", "Radiohead", "Pablo Honey"));

        Assert.Equal(IdentitySource.TagMetadata, identity.Source);
        Assert.Equal("Radiohead", identity.Artist);
        Assert.Equal("Pablo Honey", identity.Album);
        Assert.True(identity.IsTitleTrusted);
    }

    [Fact]
    public void An_untagged_file_does_not_send_the_placeholder_artist()
    {
        SongIdentity identity = Resolver.Resolve(Local("Creep", "Unknown Artist", "Unknown Album"));

        Assert.False(identity.HasArtist);
        Assert.False(identity.HasAlbum);
        Assert.Equal(IdentitySource.FileName, identity.Source);
    }

    [Fact]
    public void A_file_name_without_tags_is_still_a_song_name()
    {
        SongIdentity identity = Resolver.Resolve(
            Local("Creep", string.Empty, path: @"D:\Music\Radiohead - Creep [Remastered].flac"));

        Assert.True(identity.HasSongTitle);
        Assert.Equal(IdentitySource.FileName, identity.Source);
        Assert.False(identity.HasArtist);
    }

    [Fact]
    public void An_unknown_duration_is_absent_rather_than_zero()
    {
        SongIdentity identity = Resolver.Resolve(
            new Track("Creep", "Radiohead", TimeSpan.Zero, FilePath: @"D:\Music\Creep.flac"));

        Assert.False(identity.HasDuration);
        Assert.Null(identity.DurationSeconds);
    }

    [Fact]
    public void Chinese_titles_are_recognised_and_keep_their_version_note()
    {
        SongIdentity plain = Resolver.Resolve(Stream("月亮代表我的心【高清】", "uploader"));
        SongIdentity instrumental = Resolver.Resolve(Stream("月亮代表我的心 伴奏", "uploader"));

        Assert.Equal("月亮代表我的心", plain.SongTitle);
        Assert.Equal(SongVersion.Instrumental, instrumental.Version);
    }

    [Fact]
    public void A_separated_title_offers_both_readings_without_inventing_an_artist()
    {
        SongIdentity identity = Resolver.Resolve(Stream("Radiohead - Creep", "uploader"));

        // Either side of the dash can be the song, so both readings are offered to the matcher and
        // neither is promoted to "the artist": a wrong guess there would penalise the correct record.
        Assert.Contains("Creep", identity.TitlesToTry);
        Assert.Contains("Radiohead - Creep", identity.TitlesToTry);
        Assert.False(identity.HasArtist);

        // The title is a video title, so it is not treated as tag-grade metadata.
        Assert.Equal(IdentitySource.VideoTitle, identity.Source);
        Assert.False(identity.IsTitleTrusted);
    }

    [Fact]
    public void A_quoted_song_name_outranks_the_words_around_it()
    {
        // The reported upload: the outer words describe the recording session, the quotes name the song.
        SongIdentity identity = Resolver.Resolve(Stream("邓丽君《月亮代表我的心》百万豪装录音棚大声听", "JLRS-LeoFM"));

        Assert.Equal("月亮代表我的心", identity.SongTitle);
        Assert.Equal("邓丽君", identity.Artist);
        Assert.Equal("月亮代表我的心", identity.TitlesToTry[0]);
    }

    [Fact]
    public void A_quoted_name_inside_a_promotional_title_is_extracted()
    {
        SongIdentity identity = Resolver.Resolve(
            Stream("【4K120帧】The Beatles《Yesterday》1965纽约现场 AI修复补帧画质增强版", "up"));

        Assert.Equal("Yesterday", identity.SongTitle);
        Assert.Equal("The Beatles", identity.Artist);
    }

    [Fact]
    public void A_song_dash_performer_title_does_not_put_the_song_in_the_artist_slot()
    {
        SongIdentity identity = Resolver.Resolve(
            Stream("Creep - Radiohead 电台司令【Hi-Res】百万级装备试听", "JLRS日落fm"));

        // "Creep" is the song here, so it must never be handed to the matcher as the performer: the
        // artist penalty would then reject every correct record.
        Assert.NotEqual("Creep", identity.Artist);
        Assert.Contains("Creep", identity.TitlesToTry);
    }

    [Fact]
    public void A_search_clue_names_the_song_when_the_title_only_advertises_the_video()
    {
        // The upload title says nothing about the song; the keyword the user typed does.
        SongIdentity identity = Resolver.Resolve(
            Stream("爷青回！这首歌你绝对听过 高清", "uploader"),
            searchClue: "Creep Radiohead");

        Assert.Equal("Creep Radiohead", identity.SongTitle);
    }

    [Fact]
    public void A_search_clue_does_not_overwrite_a_title_that_already_names_the_song()
    {
        // "creep radiohead" is the artist plus the song here, so the plain title stays the song name
        // and the clue is only asked for an artist.
        SongIdentity identity = Resolver.Resolve(Stream("Creep", "uploader"), searchClue: "creep radiohead");

        Assert.Equal("Creep", identity.SongTitle);
        Assert.Null(identity.Artist);
    }

    [Fact]
    public void Short_latin_noise_words_do_not_cut_into_real_song_names()
    {
        // Each of these used to lose letters to a noise word matched as a bare substring.
        SongIdentity mvp = Resolver.Resolve(Local("MVP", "Someone"));
        SongIdentity hdmi = Resolver.Resolve(Local("HDMI", "Someone"));
        SongIdentity officialdom = Resolver.Resolve(Local("Officialdom", "Someone"));

        Assert.Equal("MVP", mvp.SongTitle);
        Assert.Equal("HDMI", hdmi.SongTitle);
        Assert.Equal("Officialdom", officialdom.SongTitle);
    }

    [Theory]
    [InlineData("Creep【高清】", "Creep")]
    [InlineData("Creep (Official Video)", "Creep")]
    [InlineData("Creep - HD", "Creep")]
    [InlineData("Creep 官方MV", "Creep")]
    public void Display_noise_is_removed_when_it_stands_on_its_own(string title, string expected)
    {
        SongIdentity identity = Resolver.Resolve(Local(title, "Radiohead"));

        Assert.Equal(expected, identity.SongTitle);
    }
}
