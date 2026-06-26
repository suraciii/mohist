using Mohist.Server.Issue.Domain.IssueTemplate;

namespace Mohist.Server.Issue.Services.IssueTemplates;

/// <summary>
/// The built-in default issue template "mohist/default".
/// Sections and guidance are transcribed from the mohist-explore skill
/// (<c>packages/cli/Mohist.Cli/skill-data/mohist-explore/SKILL.md</c>
/// and <c>references/issue-body-template.md</c>).
/// </summary>
public class MohistDefaultIssueTemplate : IIssueTemplate
{
    public string Id => IssueTemplates.DefaultId;
    public string Name => "Mohist Default";
    public string About => "Standard three-voice PRD (User Voice, Product Shape, Domain Model) with acceptance criteria and non-goals.";
    public bool IsDefault => true;

    public IReadOnlyList<string> SuitableFor { get; } = new[]
    {
        "feature development involving UI or backend changes",
        "bug fixes requiring a full plan-build-check-integrate lifecycle",
        "OpenSpec-driven workflows with structured change artifacts",
        "issues needing approval gates between stages",
    };

    public IssueTemplateDefaults Defaults => new(
        Risk: "medium",
        Workflow: "mohist/local");

    public IReadOnlyList<IssueTemplateSection> Sections { get; } = new[]
    {
        new IssueTemplateSection(
            Title: "User Voice",
            Guidance: string.Join("\n",
                "The user's own need, in the user's own words. Write from the user perspective —",
                "the scenario where this matters, the decision they cannot make, or the place they get stuck.",
                "",
                "What to write:",
                "- Who are you and what are you trying to accomplish?",
                "- Where does the current experience fail you?",
                "- What outcome would make this a success for you?",
                "",
                "What NOT to write:",
                "- How to implement it (no code, no architecture, no UI widgets)",
                "- Product terminology (no 'we should add a toggle')",
                "- Justifications or context that the user wouldn't say themselves",
                "",
                "How to write it:",
                "- Use first-person language (\"I\", \"my\", \"me\")",
                "- Minimum one sentence; expand as needed to capture the real intent",
                "- If the user proposed a solution, ask what problem it solves and record the problem"),
            Placeholder: "<Describe your need in your own words. What are you trying to do? Where do you get stuck?>"),

        new IssueTemplateSection(
            Title: "Product Shape",
            Guidance: string.Join("\n",
                "The PM translation of the User Voice into a concrete product decision.",
                "",
                "What to write:",
                "- What will the user see or be able to do after this change?",
                "- What is the boundary — what is in scope and what is explicitly NOT in scope?",
                "- What trade-offs were made (if two directions existed, which one and why)?",
                "- Cite what you actually observed in the current product form (pages, commands, flows)",
                "",
                "What NOT to write:",
                "- Implementation details (no files, functions, database tables)",
                "- Domain model concepts (that belongs in the next section)",
                "- Vague aspirations without a concrete boundary",
                "",
                "How to write it:",
                "- Write in product language, not code language",
                "- State non-goals that are brave — actually cutting things, not listing safe trivia",
                "- Every user need should trace to a product decision that addresses it"),
            Placeholder: "<What changes in the product? What is the boundary — in scope and out of scope? Make trade-offs explicit.>"),

        new IssueTemplateSection(
            Title: "Domain Model",
            Guidance: string.Join("\n",
                "The domain expert's view: key concepts, invariants, and constraints that shape the solution.",
                "This is about the PROBLEM space, not the solution space.",
                "",
                "What to write:",
                "- Key domain concepts and how they relate to each other",
                "- Invariants that must always hold true",
                "- Constraints that shape what is possible",
                "- Cite the code paths and data models you inspected",
                "",
                "What NOT to write:",
                "- Prescribed implementation (files, functions, database tables, task steps)",
                "- Full technical design — that belongs to the Plan stage",
                "- Premature optimization or architectural choices",
                "",
                "How to write it:",
                "- Keep it to the minimum domain context needed to understand the requirement",
                "- If the Product Shape turns out infeasible, say so and revise Product Shape",
                "- Use domain language, not implementation language"),
            Placeholder: "<What are the key domain concepts? What invariants and constraints shape this? Cite relevant code paths.>"),

        new IssueTemplateSection(
            Title: "Acceptance Criteria",
            Guidance: string.Join("\n",
                "Observable, verifiable conditions described from the user perspective.",
                "",
                "What to write:",
                "- Each criterion is a [ ] checklist item",
                "- Describes something the user can see or do after the change",
                "- Covers the boundary defined in Product Shape",
                "",
                "What NOT to write:",
                "- Implementation checks (no 'unit test passes', 'migration applied')",
                "- Technical verification steps",
                "- Vague or unverifiable statements",
                "",
                "How to write it:",
                "- One observable outcome per line",
                "- Group related outcomes, but keep each line independently verifiable",
                "- A reader should be able to tell whether the criterion is met without reading the code"),
            Placeholder: "- [ ] <Observable, verifiable condition from the user perspective>\n- [ ] <Observable, verifiable condition>\n- [ ] <Observable, verifiable condition>"),

        new IssueTemplateSection(
            Title: "Non-Goals",
            Guidance: string.Join("\n",
                "Explicitly out-of-scope items that clarify the boundary.",
                "",
                "What to write:",
                "- Things the reader might reasonably expect but that are deliberately excluded",
                "- Scope boundaries that prevent creep",
                "- Related features or improvements that belong in follow-up issues",
                "",
                "What NOT to write:",
                "- Obvious non-goals that nobody would expect",
                "- Restating what is already clear from the Product Shape boundary",
                "- Passive-aggressive justifications for excluding things",
                "",
                "How to write it:",
                "- Each item is a single line starting with a dash",
                "- Be specific — name the excluded feature or change",
                "- Brave non-goals make the scope clearer than the goals do"),
            Placeholder: "- <Explicit out-of-scope item>\n- <Explicit out-of-scope item>"),
    };
}
