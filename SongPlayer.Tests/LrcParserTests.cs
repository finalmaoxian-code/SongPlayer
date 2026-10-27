using SongPlayer.Services.Lyrics;

namespace SongPlayer.Tests;

/// <summary>
/// Tests for the LRC reader.
/// </summary>
/// <remarks>
/// Lyrics in the database are user-submitted, so the parser sees every shape of malformed input:
/// several timestamps on one line, translations stacked at the same timestamp, a BOM from a Windows
/// editor, an offset tag, out-of-order lines and lines that are not lyrics at all. A bad line may not
/// cost the song its lyrics, so the parser drops what it cannot read and keeps the rest.
/// </remarks>
public sealed class LrcParserTests
{
    [Fact]
    public void Hundredths_and_milliseconds_are_both_read()
    {
        LrcDocument document = LrcParser.Parse("""
            [00:01.5]tenths
            [00:02.25]hundredths
            [00:03.125]milliseconds
            [00:04]seconds
            """);

        Assert.Equal(4, document.Lines.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), document.Lines[0].Time);
        Assert.Equal(TimeSpan.FromMilliseconds(2250), document.Lines[1].Time);
        Assert.Equal(TimeSpan.FromMilliseconds(3125), document.Lines[2].Time);
        Assert.Equal(TimeSpan.FromSeconds(4), document.Lines[3].Time);
    }

    [Fact]
    public void One_line_with_several_timestamps_becomes_several_lines()
    {
        LrcDocument document = LrcParser.Parse("[00:10.00][00:40.00]chorus");

        Assert.Equal(2, document.Lines.Count);
        Assert.Equal("chorus", document.Lines[0].Text);
        Assert.Equal("chorus", document.Lines[1].Text);
    }

    [Fact]
    public void A_translation_at_the_same_timestamp_is_kept_with_the_original()
    {
        LrcDocument document = LrcParser.Parse("""
            [00:05.00]When you were here before
            [00:05.00]当你在这里的时候
            """);

        Assert.Single(document.Lines);
        Assert.Contains("When you were here before", document.Lines[0].Text, StringComparison.Ordinal);
        Assert.Contains("当你在这里的时候", document.Lines[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Lines_are_sorted_by_time_even_when_the_file_is_not()
    {
        LrcDocument document = LrcParser.Parse("""
            [00:30.00]third
            [00:10.00]first
            [00:20.00]second
            """);

        Assert.Collection(
            document.Lines,
            line => Assert.Equal("first", line.Text),
            line => Assert.Equal("second", line.Text),
            line => Assert.Equal("third", line.Text));
    }

    [Fact]
    public void A_bom_crlf_metadata_tags_and_an_offset_tag_are_handled()
    {
        LrcDocument document = LrcParser.Parse(
            "\uFEFF[ar:Radiohead]\r\n[ti:Creep]\r\n[offset:-500]\r\n[00:10.00]line\r\n");

        Assert.Single(document.Lines);
        Assert.Equal(TimeSpan.FromMilliseconds(9500), document.Lines[0].Time);
        Assert.Equal(TimeSpan.FromMilliseconds(-500), document.FileOffset);
    }

    [Fact]
    public void An_offset_that_would_push_a_line_before_the_start_is_clamped()
    {
        LrcDocument document = LrcParser.Parse("""
            [offset:-5000]
            [00:01.00]early
            [00:20.00]later
            """);

        Assert.Equal(TimeSpan.Zero, document.Lines[0].Time);
        Assert.Equal(TimeSpan.FromSeconds(15), document.Lines[1].Time);
    }

    [Fact]
    public void Malformed_timestamps_and_junk_lines_do_not_cost_the_song_its_lyrics()
    {
        LrcDocument document = LrcParser.Parse("""
            [00:05.00]kept
            [99:99.999]impossible
            [abc]not a timestamp
            no tag at all
            [00:06.00]
            [00:07.00]also kept
            """);

        Assert.Equal(2, document.Lines.Count);
        Assert.Equal("kept", document.Lines[0].Text);
        Assert.Equal("also kept", document.Lines[1].Text);
    }

    [Fact]
    public void Empty_input_yields_an_empty_document()
    {
        Assert.True(LrcParser.Parse(null).IsEmpty);
        Assert.True(LrcParser.Parse(string.Empty).IsEmpty);
        Assert.True(LrcParser.Parse("[ar:OnlyMetadata]").IsEmpty);
    }

    [Fact]
    public void Timing_survives_a_round_trip_through_the_cache_format()
    {
        // The cache stores lyrics back as LRC text, so the writer and the reader have to agree.
        IReadOnlyList<SongPlayer.Models.LyricLine> lines =
        [
            new(TimeSpan.FromMilliseconds(1234), "first"),
            new(TimeSpan.FromSeconds(61), "second"),
        ];

        string rendered = string.Concat(
            lines.Select(line => $"[{(int)line.Time.TotalMinutes:00}:{line.Time.Seconds:00}.{line.Time.Milliseconds:000}]{line.Text}\n"));

        LrcDocument document = LrcParser.Parse(rendered);

        Assert.Equal(2, document.Lines.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1234), document.Lines[0].Time);
        Assert.Equal(TimeSpan.FromSeconds(61), document.Lines[1].Time);
        Assert.Equal("second", document.Lines[1].Text);
    }
}
