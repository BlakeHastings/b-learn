using System.Text;
using BLearn.Domain;

namespace BLearn.Application;

/// <summary>
/// The briefing an agent reads before working on a store. Kept short on purpose: rules that code
/// enforces are stated once, because the CLI rejects violations with a reason anyway.
/// </summary>
public static class AgentBrief
{
    public static string Render(StoreInfo info, string exe)
    {
        var s = $"{exe} --store \"{info.Path}\"";
        var sb = new StringBuilder();
        sb.AppendLine($"# b-learn agent brief: {info.Name}");
        sb.AppendLine();
        sb.AppendLine("You build a concept graph for a learner. Every fact and relationship needs evidence from a source of truth.");
        sb.AppendLine("Your own knowledge is a lead for where to look, never evidence.");
        sb.AppendLine();
        sb.AppendLine("## Sources of truth for this store");
        if (info.Sources.Count == 0) sb.AppendLine("None registered. Ask the user which sources count as truth.");
        foreach (var src in info.Sources)
            sb.AppendLine($"- {src.Name} ({src.System}): {src.Scope}. Minimum trust: {src.MinTrust}.");
        sb.AppendLine("Only cite a source for claims inside its scope.");
        if (info.Private)
        {
            sb.AppendLine();
            sb.AppendLine("This store is PRIVATE. Never put its content (names, clauses, figures) into web searches or other external services.");
        }
        sb.AppendLine();
        sb.AppendLine("## Loop");
        sb.AppendLine($"1. Run in a background shell and wait for it to exit: {s} queue receive --wait --session <your-session-id>");
        sb.AppendLine("2. Do the work for the message's kind (below).");
        sb.AppendLine($"3. {s} queue ack <receipt>  (or: queue nack <receipt> --error \"why\")");
        sb.AppendLine("4. Go back to step 1. Exit code 3 means the wait timed out: just wait again.");
        sb.AppendLine();
        sb.AppendLine("## Kinds");
        sb.AppendLine("- expand: research a stub. Capture evidence, submit facts, add sourced edges to new or existing concepts, then write teaching.");
        sb.AppendLine("- teach: write or improve teaching for a concept (or one fact) using only its facts.");
        sb.AppendLine("- ask: answer the learner's question from the graph's facts; research more if needed.");
        sb.AppendLine("- recall: prepare questions that test the concept's facts.");
        sb.AppendLine();
        sb.AppendLine("## Commands");
        sb.AppendLine($"- Look before creating: {s} concept search <text>, concept get <id>, concept neighbors <id>");
        sb.AppendLine($"- Evidence you retrieved yourself: {s} evidence capture <id> --source <name> --locator <url-or-id> --quote \"<verbatim>\" --snapshot-file <file-with-full-text> --session <id>");
        sb.AppendLine($"- Evidence blearn can fetch (web, file): {s} evidence fetch <id> --source <name> --url <url> --quote \"<verbatim>\" --session <id>");
        sb.AppendLine($"- Facts: {s} concept facts <id> --file facts.json   (JSON: [{{\"text\": \"...\", \"evidence\": [\"ev-id\"]}}])");
        sb.AppendLine($"- New concept: {s} concept stub --id <id> --title \"...\" --gloss \"...\" [--from <parent> --type <edge-type> --evidence <ev-id>]");
        sb.AppendLine($"- Edge: {s} edge add <from> <type> <to> --evidence <ev-id>");
        sb.AppendLine($"- Teaching: {s} concept teaching <id> --summary-file s.md --explanation-file e.md");
        sb.AppendLine();
        sb.AppendLine("## Rules");
        sb.AppendLine("- Quotes are verbatim from the snapshot. Snapshots hold the full retrieved text.");
        sb.AppendLine("- Facts are atomic: one claim each.");
        sb.AppendLine("- Teaching (summary, explanation) may explain and use analogies but must not add facts that are not in the Facts list.");
        sb.AppendLine("- If you cannot find a source, drop the claim. \"Nothing further in scope\" is a valid result.");
        sb.AppendLine("- Search for an existing concept before creating one.");
        sb.AppendLine("- Edge types (direction from source to target):");
        foreach (var t in EdgeTypes.List)
            sb.AppendLine($"  - {t.Name} ({t.Direction.ToString().ToLowerInvariant()}): {t.Meaning}");
        sb.AppendLine("- A rejected command prints the rule it broke. Fix the input; do not work around the rule.");
        return sb.ToString();
    }
}
