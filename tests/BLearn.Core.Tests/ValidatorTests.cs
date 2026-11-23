using BLearn.Core;

namespace BLearn.Core.Tests;

public sealed class ValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blearn-tests", Guid.NewGuid().ToString("N"));

    public ValidatorTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "notes"));
        File.WriteAllText(Path.Combine(_root, "blearn.yaml"), "name: Test\nroot: top\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Note(string id, string frontmatterExtra = "", string? body = null, string status = "complete", string? title = null)
    {
        body ??= status == "stub" ? "" : "## Summary\n\nSummary text.\n\n## Detail\n\nDetail text.\n";
        File.WriteAllText(
            Path.Combine(_root, "notes", id + ".md"),
            $"---\nid: {id}\ntitle: {title ?? id}\nstatus: {status}\ngloss: Gloss for {id}.\n{frontmatterExtra}---\n{body}");
    }

    private List<string> Codes() =>
        Validator.Validate(Store.Load(_root)).Where(i => i.Severity == Severity.Error).Select(i => i.Code).ToList();

    [Fact]
    public void ValidStorePasses()
    {
        Note("top", "edges:\n  built-on: [mid]\n", "## Summary\n\nUses [[mid]].\n\n## Detail\n\nMore.\n");
        Note("mid", "edges:\n  built-on: [low]\n");
        Note("low", status: "stub");
        Assert.Empty(Codes());
    }

    [Fact]
    public void UnknownEdgeTypeIsRejected()
    {
        Note("top", "edges:\n  sort-of-like: [low]\n");
        Note("low", status: "stub");
        Assert.Contains("edge-type", Codes());
    }

    [Fact]
    public void EdgeToMissingNoteIsRejected()
    {
        Note("top", "edges:\n  requires: [ghost]\n");
        Assert.Contains("edge-dangling", Codes());
    }

    [Fact]
    public void DownwardCycleIsRejected()
    {
        Note("top", "edges:\n  built-on: [a]\n");
        Note("a", "edges:\n  requires: [b]\n");
        Note("b", "edges:\n  is-a: [a]\n");
        Assert.Contains("edge-cycle", Codes());
    }

    [Fact]
    public void LateralEdgesMayFormLoops()
    {
        Note("top", "edges:\n  related: [a]\n");
        Note("a", "edges:\n  contrasts-with: [b]\n");
        Note("b", "edges:\n  related: [top]\n");
        Assert.Empty(Codes());
    }

    [Fact]
    public void SameRelationshipStoredOnBothSidesIsRejected()
    {
        Note("top", "edges:\n  related: [a]\n");
        Note("a", "edges:\n  related: [top]\n");
        Assert.Contains("edge-both-sides", Codes());
    }

    [Fact]
    public void TwoEdgesToSameTargetAreRejected()
    {
        Note("top", "edges:\n  requires: [a]\n  related: [a]\n");
        Note("a", status: "stub");
        Assert.Contains("edge-multiple", Codes());
    }

    [Fact]
    public void DuplicateNameAcrossNotesIsRejected()
    {
        Note("top", "aliases: [Pointer]\n");
        Note("pointer", title: "pointer");
        Assert.Contains("name-duplicate", Codes());
    }

    [Fact]
    public void InlineLinkWithoutEdgeIsRejected()
    {
        Note("top", "edges:\n  requires: [a]\n", "## Summary\n\nSee [[b]].\n\n## Detail\n\nMore.\n");
        Note("a", "edges:\n  requires: [b]\n");
        Note("b", status: "stub");
        Assert.Contains("link-no-edge", Codes());
    }

    [Fact]
    public void InlineLinkToIncomingEdgeIsAllowed()
    {
        Note("top", "edges:\n  requires: [a]\n");
        Note("a", body: "## Summary\n\nUsed by [[top]].\n\n## Detail\n\nMore.\n");
        Assert.Empty(Codes());
    }

    [Fact]
    public void LinksInsideCodeFencesAreIgnored()
    {
        Note("top", body: "## Summary\n\nText.\n\n## Detail\n\n```\nx[[nothing]]\n```\n");
        Assert.Empty(Codes());
    }

    [Fact]
    public void StubWithBodyIsRejected()
    {
        Note("top", "edges:\n  requires: [a]\n");
        Note("a", status: "stub", body: "## Summary\n\nOops.\n");
        Assert.Contains("stub-body", Codes());
    }

    [Fact]
    public void CompleteNoteNeedsExactSections()
    {
        Note("top", body: "## Summary\n\nText.\n\n## Notes\n\nWrong heading.\n");
        Assert.Contains("body-shape", Codes());
    }

    [Fact]
    public void FilenameMustMatchId()
    {
        File.WriteAllText(Path.Combine(_root, "notes", "top.md"),
            "---\nid: something-else\ntitle: T\nstatus: stub\ngloss: G.\n---\n");
        Assert.Contains("id-filename", Codes());
    }

    [Fact]
    public void UnknownFrontmatterFieldIsRejected()
    {
        Note("top", "difficulty: hard\n");
        Assert.Contains("field-unknown", Codes());
    }

    [Fact]
    public void OverlongGlossIsRejected()
    {
        File.WriteAllText(Path.Combine(_root, "notes", "top.md"),
            $"---\nid: top\ntitle: T\nstatus: stub\ngloss: {new string('x', 201)}\n---\n");
        Assert.Contains("gloss-format", Codes());
    }

    [Fact]
    public void GraphInvertsDirectionForTheTargetSide()
    {
        Note("top", "edges:\n  built-on: [low]\n");
        Note("low", status: "stub");
        var graph = new Graph(Store.Load(_root).Notes);
        Assert.Equal(Direction.Down, graph.Relation("top", "low")!.Direction);
        var up = graph.Relation("low", "top")!;
        Assert.Equal(Direction.Up, up.Direction);
        Assert.Equal("underlies", up.Label);
    }
}
