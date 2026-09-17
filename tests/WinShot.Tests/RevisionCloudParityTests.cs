using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using WinShot.Editor;
using Xunit;
using Xunit.Abstractions;

namespace WinShot.Tests;

/// <summary>
/// Diffs <see cref="RevisionCloud"/> against Survey's approved reference engine
/// (<c>~/Survey/tests/fixtures/reference/cloud-v17.ts</c>, studio <c>lib/cloud.ts</c> @ d1abe78,
/// tag <c>cloud-final-short-gap</c>) the same way Survey's own
/// <c>revisionCloudGeometryParity.test.mjs</c> diffs its JS port against it.
///
/// Fixtures/revision-cloud-v17.json is the reference module's OWN output, dumped straight
/// from node — every run's id and its rendered <c>d</c> (overlap-trimmed crowns plus their
/// separator tails), not a re-derivation. Regenerate it by running the reference's
/// <c>cloudRuns</c> over the same case list; never hand-edit it to make a test pass.
///
/// TOLERANCE. Everything is compared numerically, not as strings: the engine rounds every
/// coordinate to 5 decimals, so 1e-5 is its own quantum and nothing can be asserted tighter
/// than the grid the reference prints on. The port matches structure EXACTLY (run count,
/// run ids, command count, verbs) and the residual is only the last bits of
/// <c>cos/sin/atan2/acos/tanh</c>, which V8 and the Windows CRT need not round the same way;
/// the test reports the worst deviation it actually measured so any real drift shows up as a
/// number rather than as a pass.
/// </summary>
public class RevisionCloudParityTests(ITestOutputHelper output)
{
    /// <summary>The 5-decimal grid the reference prints on — nothing can be asserted tighter.</summary>
    private const double Tolerance = 1e-5;

    private sealed record Fixture(string Label, string Kind, double Size, double Depth,
        double[][] Points, FixtureRun[] Runs);

    private sealed record FixtureRun(string Id, string D);

    private static Fixture[] Load()
    {
        string path = Path.Combine(TestPaths.RepoRoot(), "tests", "WinShot.Tests", "Fixtures", "revision-cloud-v17.json");
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<Fixture[]>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static CloudKind KindOf(string kind) => kind switch
    {
        "rectangle" => CloudKind.Rectangle,
        "ellipse" or "circle" => CloudKind.Ellipse,
        "polyline" or "freehand" => CloudKind.Polyline,
        _ => CloudKind.Polygon,
    };

    /// <summary>The reference emits only absolute <c>M</c> and <c>C</c>, so this parser is total.</summary>
    private static List<CloudCommand> Parse(string d)
    {
        var commands = new List<CloudCommand>();
        string[] tokens = d.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length;)
        {
            double N(int k) => double.Parse(tokens[i + k], CultureInfo.InvariantCulture);
            if (tokens[i] == "M")
            {
                commands.Add(CloudCommand.Move(new Point(N(1), N(2))));
                i += 3;
            }
            else
            {
                Assert.Equal("C", tokens[i]);
                commands.Add(CloudCommand.Curve(new Point(N(1), N(2)), new Point(N(3), N(4)), new Point(N(5), N(6))));
                i += 7;
            }
        }
        return commands;
    }

    private static IEnumerable<double> Coordinates(CloudCommand c) =>
        c.Verb == 'M' ? [c.P1.X, c.P1.Y] : [c.P1.X, c.P1.Y, c.P2.X, c.P2.Y, c.P3.X, c.P3.Y];

    /// <summary>One crown: its leading <c>M</c> and the cubics that follow it.</summary>
    private static List<List<CloudCommand>> Crowns(IEnumerable<CloudCommand> commands)
    {
        var crowns = new List<List<CloudCommand>>();
        foreach (CloudCommand c in commands)
        {
            if (c.Verb == 'M') crowns.Add([]);
            crowns[^1].Add(c);
        }
        return crowns;
    }

    private sealed class Diff
    {
        public double Worst;
        public int Crowns;
        public readonly List<string> HookFlips = [];
        public readonly List<string> Failures = [];
    }

    /// <summary>
    /// Compare one case against the reference. Structure — run count, run ids, crown count,
    /// crown anchors — is compared EXACTLY; coordinates within <see cref="Tolerance"/>.
    ///
    /// The one thing counted rather than asserted is whether a crown grew its separator
    /// hook, because the reference decides that on a knife edge: <c>replacementTail</c>
    /// builds one only when <c>d / w &lt; 0.88</c>, and on a uniform row <c>d / w</c> IS
    /// 0.88 — the reference's own value lands on either side of it from one crown to the
    /// next (measured in ONE row of a 1000x700 sheet: 13 crowns at 0.8799999999999992, 4 at
    /// 0.8800000000000011). .NET's Math.Sin is one ulp from V8's at the 45-degree corner
    /// bisector, which is enough to carry a crown across that edge — which is why
    /// RevisionCloud carries fdlibm's kernels. A flipped crown would keep its anchor and its
    /// rising quarter-ellipse and differ only in how its descending half is trimmed, so
    /// those two commands stay asserted while the flip itself is counted; the count is
    /// asserted to be zero, so this is a diagnostic rather than an allowance.
    /// </summary>
    private static void Compare(Fixture fixture, Diff diff)
    {
        var points = fixture.Points.Select(p => new Point(p[0], p[1])).ToArray();
        List<CloudRun> actual = RevisionCloud.CloudRuns(
            RevisionCloud.MakeShape(KindOf(fixture.Kind), points, fixture.Size, fixture.Depth));

        void Fail(string message) => diff.Failures.Add($"{fixture.Label}: {message}");

        if (actual.Count != fixture.Runs.Length)
        {
            Fail($"run count {actual.Count} != {fixture.Runs.Length}");
            return;
        }
        for (int r = 0; r < actual.Count; r++)
        {
            // The reference prefixes every id with the shape id ('x' in the fixture);
            // WinShot has no per-shape id, so only the role and index are compared.
            string expectedId = fixture.Runs[r].Id[(fixture.Runs[r].Id.IndexOf(':') + 1)..];
            string actualId = actual[r].Id[(actual[r].Id.IndexOf(':') + 1)..];
            if (actualId != expectedId)
            {
                Fail($"run {r} id '{actualId}' != '{expectedId}'");
                return;
            }

            List<List<CloudCommand>> expected = Crowns(Parse(fixture.Runs[r].D));
            List<List<CloudCommand>> mine = Crowns(actual[r].Commands);
            if (mine.Count != expected.Count)
            {
                Fail($"run {r} ({expectedId}) crown count {mine.Count} != {expected.Count}");
                return;
            }
            diff.Crowns += expected.Count;
            for (int c = 0; c < expected.Count; c++)
            {
                bool flipped = mine[c].Count != expected[c].Count;
                // A flipped crown keeps its anchor and its rising quarter-ellipse: M, then
                // the cubic that ends on the apex. Everything else is compared in full.
                int shared = flipped ? 2 : expected[c].Count;
                if (flipped) diff.HookFlips.Add($"{fixture.Label} run {r} ({expectedId}) crown {c}");
                for (int k = 0; k < shared; k++)
                {
                    if (mine[c][k].Verb != expected[c][k].Verb)
                    {
                        Fail($"run {r} ({expectedId}) crown {c} segment {k} verb");
                        return;
                    }
                    double[] a = [.. Coordinates(mine[c][k])];
                    double[] e = [.. Coordinates(expected[c][k])];
                    for (int i = 0; i < a.Length; i++)
                    {
                        double delta = Math.Abs(a[i] - e[i]);
                        if (delta > diff.Worst) diff.Worst = delta;
                        if (delta > Tolerance)
                        {
                            Fail($"run {r} ({expectedId}) crown {c} segment {k} coord {i}: {a[i]} != {e[i]} (delta {delta})");
                            return;
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void EveryFixtureMatchesTheApprovedReference()
    {
        Fixture[] fixtures = Load();
        Assert.True(fixtures.Length > 400, $"fixture set shrank to {fixtures.Length}");

        var diff = new Diff();
        foreach (Fixture fixture in fixtures) Compare(fixture, diff);

        output.WriteLine($"{fixtures.Length} fixtures, {diff.Crowns} crowns, "
            + $"worst coordinate deviation {diff.Worst:G17}, "
            + $"{diff.HookFlips.Count} separator-hook flips");
        foreach (string flip in diff.HookFlips) output.WriteLine($"hook flip: {flip}");
        foreach (string failure in diff.Failures) output.WriteLine(failure);
        Assert.Empty(diff.Failures);
        // The engine rounds to 5 decimals, so this is the grid it prints on: a real porting
        // bug cannot hide under it, and every coordinate measured so far lands on 0.
        Assert.True(diff.Worst <= Tolerance, $"worst deviation {diff.Worst:G17}");
        // Zero, since RevisionCloud evaluates sin/cos the way V8 does inside the kernel
        // range. Before that it was 18 crowns on the 1000x700 sheet; if this ever comes back
        // the offending crowns are printed above.
        Assert.Empty(diff.HookFlips);
    }

    /// <summary>
    /// The families the Cloud style is offered on all have to be covered, so a shrunken
    /// fixture file cannot quietly turn the suite above into a rectangles-only test.
    /// </summary>
    [Theory]
    [InlineData("rectangle 300x200 bump=1")]
    [InlineData("rectangle 300x200 bump=5")]
    [InlineData("rectangle 300x200 bump=20")]
    [InlineData("ellipse 300x200 bump=2")]
    [InlineData("polygon 300x200 bump=2")]
    [InlineData("polygon concave size=28 depth=12")]
    [InlineData("polygon star size=28 depth=12")]
    [InlineData("polygon shortGapConvex size=28 depth=12")]
    [InlineData("polygon counterClockwise size=28 depth=12")]
    [InlineData("polyline zigzag size=28")]
    [InlineData("polyline twoPoint size=28")]
    [InlineData("polygon tiny bump=20")]
    [InlineData("rectangle 4x4 size=120 depth=40")]
    [InlineData("rectangle 0.5x0.5 size=28")]
    [InlineData("ellipse 600x18 size=28 depth=12")]
    public void NamedShapeMatchesTheApprovedReference(string label)
    {
        Fixture fixture = Load().Single(f => f.Label == label);
        var diff = new Diff();
        Compare(fixture, diff);
        Assert.Empty(diff.Failures);
        Assert.Empty(diff.HookFlips);
        output.WriteLine($"{label}: {fixture.Runs.Length} runs, worst deviation {diff.Worst:G17}");
    }

    /// <summary>A shape the engine cannot draw produces nothing, never a stray subpath.</summary>
    [Fact]
    public void DegenerateInputProducesNoRuns()
    {
        Assert.Empty(RevisionCloud.CloudRuns(RevisionCloud.MakeShape(
            CloudKind.Rectangle, [new Point(0, 0), new Point(10, 0), new Point(10, 0), new Point(0, 0)])));
        Assert.Empty(RevisionCloud.CloudRuns(RevisionCloud.MakeShape(
            CloudKind.Polygon, [new Point(5, 5)])));
    }

    /// <summary>
    /// Everything the engine emits is a cubic: one <c>M</c> per crown (each crown is its own
    /// open subpath) and <c>C</c> for every segment. Nothing else is ever produced.
    /// </summary>
    [Fact]
    public void OutlineIsOnlyAbsoluteMoveAndCubic()
    {
        List<CloudCommand> outline = RevisionCloud.Outline(CloudKind.Rectangle,
            [new Point(0, 0), new Point(300, 0), new Point(300, 200), new Point(0, 200)]);
        Assert.NotEmpty(outline);
        Assert.Equal('M', outline[0].Verb);
        Assert.All(outline, c => Assert.Contains(c.Verb, "MC"));
        Assert.All(outline, c => Assert.All(Coordinates(c), v => Assert.True(double.IsFinite(v))));
    }

    /// <summary>
    /// Every coordinate leaves the engine on the reference's 5-decimal grid, because that
    /// rounding is part of the approved output rather than a formatting choice.
    /// </summary>
    [Fact]
    public void CoordinatesAreRoundedToFiveDecimals()
    {
        foreach (CloudCommand c in RevisionCloud.Outline(CloudKind.Polygon,
            [new Point(0, 0), new Point(137.3137, 11.919), new Point(91.777, 203.0101)]))
        {
            foreach (double v in Coordinates(c)) Assert.Equal(v, RevisionCloud.Round5(v));
        }
    }
}
