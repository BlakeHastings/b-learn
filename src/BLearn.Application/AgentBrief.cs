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
        sb.AppendLine("You build a concept graph for a learner and answer their questions. Every fact and relationship needs evidence from a source of truth.");
        sb.AppendLine("Your own knowledge is a lead for where to look, never evidence.");
        sb.AppendLine();
        sb.AppendLine("## Sources of truth for this store");
        if (info.Sources.Count == 0)
        {
            sb.AppendLine("None registered. Ask the user which sources count as truth before starting, then register each:");
            sb.AppendLine($"  {s} source post <name> --system <web|file|google-drive|...> --scope \"what it is authoritative about\"");
            sb.AppendLine("  --system is where it lives; blearn can fetch web and file sources itself, other systems you read through your own tools.");
            sb.AppendLine("  --min-trust (default captured) is the weakest evidence the UI shows as verified: captured < reconfirmed < fetched.");
        }
        foreach (var src in info.Sources)
            sb.AppendLine($"- {src.Name} ({src.System}): {src.Scope}. Minimum trust: {src.MinTrust}.");
        sb.AppendLine("Only cite a source for claims inside its scope.");
        if (info.Private)
        {
            sb.AppendLine();
            sb.AppendLine("This store is PRIVATE. Never put its content (names, clauses, figures) into web searches or other external services.");
        }
        sb.AppendLine();
        sb.AppendLine("## How to work: orchestrate");
        sb.AppendLine("You coordinate; subagents do the work, in parallel. Pick a session id for yourself (your harness's session id, or agent-<random>).");
        sb.AppendLine($"1. Wait for work in a background shell: {s} lease post --wait --session <id>. Then claim more without waiting");
        sb.AppendLine($"   ({s} lease post --session <id>) until you hold up to 4 messages or the queue is empty.");
        sb.AppendLine("2. Give each message to its own subagent: this brief, the message payload, and its own session id (<id>-1, <id>-2, ...).");
        sb.AppendLine("   Keep the receipts yourself. Inside a large expand, a subagent may split source reading further.");
        sb.AppendLine($"3. When a subagent finishes: {s} lease delete <receipt> (or lease patch <receipt> --release --error \"why\"), then claim the next message.");
        sb.AppendLine("4. Start ask messages first: a learner is waiting. Give a check only to a subagent that did not research that concept.");
        sb.AppendLine("5. Exit code 3 means the wait timed out: wait again unless your session should stop.");
        sb.AppendLine("If your harness cannot run subagents, do the messages one at a time yourself.");
        sb.AppendLine("lease delete is refused until the result is in the store. If there is honestly nothing to produce (no source in scope),");
        sb.AppendLine("finish with lease delete <receipt> --without-outcome \"why\".");
        sb.AppendLine();
        sb.AppendLine("## Kinds");
        sb.AppendLine("- expand: research a stub or unverified concept. Read sources, post evidence, put facts, post sourced edges, then put teaching.");
        sb.AppendLine("  Revise the gloss (concept patch --gloss) if it is a placeholder or inaccurate.");
        sb.AppendLine("  Put each fact on the concept it is about; create that concept if needed.");
        sb.AppendLine("  Leave new stubs as stubs: the learner's visits queue their research.");
        sb.AppendLine("  Unverified concepts carry imported teaching and edges: treat them as leads, cite or delete each edge, replace the teaching.");
        sb.AppendLine("- teach: rewrite the concept's teaching (summary, explanation) from its facts.");
        sb.AppendLine("- ask: answer the learner in the conversation (reply post). The payload has the question, the highlighted quote and fact index if any.");
        sb.AppendLine("  Answer from the graph's facts. If you need new facts, research and put them on the right concept first, then answer.");
        sb.AppendLine("  Keep it conversational and short; mention concepts by name or as [[concept-id]] so the learner can hover and dive in.");
        sb.AppendLine("- recall: write quiz questions that test the concept's facts (recall-question put).");
        sb.AppendLine("- check: review a concept you did not research, in a fresh session. For every fact, read its quotes and give a verdict:");
        sb.AppendLine("  supports (the quotes back the whole fact), partial (say what is unsupported), unsupported. Also flag gloss or teaching");
        sb.AppendLine("  passages that state something no fact supports (check put). Your session must differ from the one that captured the evidence.");
        sb.AppendLine();
        sb.AppendLine("## Commands (blearn <resource> <verb>; blearn <resource> --help for one resource)");
        sb.AppendLine($"- Look before creating: {s} concept list --search <text>, concept get <id>, neighbor list <id>");
        sb.AppendLine($"- Read a page as blearn stores it, to copy exact quotes: {s} page get --url <url>");
        sb.AppendLine($"- Evidence blearn fetches (web, file): {s} evidence post <id> --source <name> --url <url> --quote \"<verbatim>\" --session <id>");
        sb.AppendLine($"- Evidence you retrieved yourself: {s} evidence post <id> --source <name> --locator <url-or-id> --snapshot-file <full-text-file> --quote \"<verbatim>\" --session <id>");
        sb.AppendLine($"- Remove evidence nothing cites: {s} evidence delete <id>");
        sb.AppendLine($"- Facts: {s} facts put <concept> --file facts.json   (JSON: [{{\"text\": \"...\", \"evidence\": [\"ev-id\"]}}]). PUT replaces all facts: send the full list.");
        sb.AppendLine("  Fact indexes start at 0, in the order you put them.");
        sb.AppendLine($"- New concept: {s} concept post --id <id> --title \"...\" --gloss \"...\" [--from <parent> --type <edge-type> --evidence <ev-id>]");
        sb.AppendLine($"- Edge: {s} edge post <from> <type> <to> --evidence <ev-id>   |   cite an unverified edge: {s} edge-evidence post <from> <to> --evidence <ev-id>   |   {s} edge delete <from> <to>");
        sb.AppendLine("  <from> and <to> are the edge as stored: the type reads from <from> to <to>. neighbor list shows the stored source and target.");
        sb.AppendLine($"- Teaching: {s} teaching put <concept> --summary-file s.md --explanation-file e.md");
        sb.AppendLine($"- Reply: {s} reply post <conversation-id> --text-file answer.md --session <id>   |   history: conversation get <conversation-id>");
        sb.AppendLine($"- Quiz: {s} recall-question put <concept> --file q.json   (JSON: [{{\"prompt\": \"...\", \"answer\": \"...\", \"facts\": [0]}}])");
        sb.AppendLine($"- Check: {s} check put <concept> --file check.json --session <id>   (blearn check --help shows the JSON)");
        sb.AppendLine();
        sb.AppendLine("## Rules");
        sb.AppendLine("- Quotes are verbatim from the page text. One evidence item per quote.");
        sb.AppendLine("- Facts are atomic: one claim each.");
        sb.AppendLine("- Teaching and replies may explain and use analogies but must not add facts that are not in the Facts list.");
        sb.AppendLine("- If you cannot find a source, drop the claim. \"Nothing further in scope\" is a valid result.");
        sb.AppendLine("- Search for an existing concept before creating one.");
        sb.AppendLine("- Edge types (direction from source to target). Foundations a concept rests on are down edges (built-on, requires, is-a), not related:");
        foreach (var t in EdgeTypes.List)
            sb.AppendLine($"  - {t.Name} ({t.Direction.ToString().ToLowerInvariant()}): {t.Meaning}");
        sb.AppendLine("- A rejected command prints the rule it broke. Fix the input; do not work around the rule.");
        return sb.ToString();
    }
}
