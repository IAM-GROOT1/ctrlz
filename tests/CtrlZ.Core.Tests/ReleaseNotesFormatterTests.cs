using CtrlZ.Core.Updates;

namespace CtrlZ.Core.Tests;

public class ReleaseNotesFormatterTests
{
    [Fact]
    public void Empty_or_missing_notes_become_empty_text()
    {
        Assert.Equal(string.Empty, ReleaseNotesFormatter.ToPlainText(null));
        Assert.Equal(string.Empty, ReleaseNotesFormatter.ToPlainText(""));
        Assert.Equal(string.Empty, ReleaseNotesFormatter.ToPlainText("   \n  "));
    }

    [Fact]
    public void Strips_html_tags_instead_of_rendering_them()
    {
        var text = ReleaseNotesFormatter.ToPlainText("Fixes <b>bold</b> and <script>alert('x')</script> text.");

        Assert.DoesNotContain("<", text);
        Assert.DoesNotContain(">", text);
        Assert.DoesNotContain("script", text);
        Assert.Contains("Fixes bold and alert('x') text.", text);
    }

    [Fact]
    public void Removes_img_and_iframe_content()
    {
        var text = ReleaseNotesFormatter.ToPlainText("Before <img src=\"http://evil/x.png\"> after.");

        Assert.DoesNotContain("evil", text);
        Assert.Contains("Before", text);
        Assert.Contains("after.", text);
    }

    [Fact]
    public void Converts_markdown_links_to_plain_text()
    {
        var text = ReleaseNotesFormatter.ToPlainText("See [the notes](https://example.com/notes) for details.");

        Assert.Contains("the notes (https://example.com/notes)", text);
    }

    [Fact]
    public void Converts_images_to_their_alt_text_only()
    {
        var text = ReleaseNotesFormatter.ToPlainText("![screenshot](https://example.com/s.png)");

        Assert.Equal("screenshot", text);
    }

    [Fact]
    public void Removes_heading_markers_but_keeps_the_words()
    {
        var text = ReleaseNotesFormatter.ToPlainText("## What changed\n\nFixed the thing.");

        Assert.DoesNotContain("#", text);
        Assert.Contains("What changed", text);
        Assert.Contains("Fixed the thing.", text);
    }

    [Fact]
    public void Converts_bullets_to_a_bullet_character()
    {
        var text = ReleaseNotesFormatter.ToPlainText("- one\n- two\n* three");

        Assert.Contains("\u2022 one", text);
        Assert.Contains("\u2022 two", text);
        Assert.Contains("\u2022 three", text);
    }

    [Fact]
    public void Removes_emphasis_and_inline_code_markers()
    {
        var text = ReleaseNotesFormatter.ToPlainText("**Important** and `code` and __also__");

        Assert.Equal("Important and code and also", text);
    }

    [Fact]
    public void Removes_code_fence_lines()
    {
        var text = ReleaseNotesFormatter.ToPlainText("Text\n```powershell\ndotnet build\n```\nMore");

        Assert.DoesNotContain("```", text);
        Assert.Contains("dotnet build", text);
    }

    [Fact]
    public void Collapses_excess_blank_lines()
    {
        var text = ReleaseNotesFormatter.ToPlainText("a\n\n\n\n\nb");

        Assert.Equal("a\n\nb", text);
    }

    [Fact]
    public void Normalizes_windows_line_endings()
    {
        var text = ReleaseNotesFormatter.ToPlainText("a\r\nb\rc");

        Assert.DoesNotContain("\r", text);
    }

    [Fact]
    public void Drops_control_characters()
    {
        var text = ReleaseNotesFormatter.ToPlainText("a\u0000b\u0007c");

        Assert.Equal("abc", text);
    }

    [Fact]
    public void Truncates_long_notes_and_marks_the_cut()
    {
        var notes = string.Join(' ', Enumerable.Repeat("word", 500));

        var text = ReleaseNotesFormatter.ToPlainText(notes, maxLength: 100);

        Assert.True(text.Length <= 101, $"was {text.Length}");
        Assert.EndsWith("\u2026", text);
        Assert.DoesNotContain("  ", text);
    }

    [Fact]
    public void Short_notes_are_not_truncated()
    {
        var text = ReleaseNotesFormatter.ToPlainText("Short notes.", maxLength: 100);

        Assert.Equal("Short notes.", text);
    }

    [Fact]
    public void Rejects_a_nonsensical_limit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReleaseNotesFormatter.ToPlainText("x", maxLength: 0));
    }
}
