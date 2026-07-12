using Circuit;

namespace Circuit.Interop.Tests.DocumentationExamples;

internal static class SkillsSecurityExample
{
    public static ResolvedSkill CreateResolvedSkill()
    {
        var reference = SkillReference.CreateInline(
            "skill.inline-style",
            "1.0.0",
            "Use a calm, concise support tone.",
            "Inline support guidance.");

        return new ResolvedSkill(
            reference,
            new Dictionary<string, object?>
            {
                ["audience"] = "premium-support",
                ["revision"] = 3,
            });
    }

    public static SkillReference CreateFileSkill()
        => SkillReference.CreateFile(
            "skill.file-style",
            "1.0.0",
            ["/srv/circuit/skills/support-style"],
            "File-backed guidance with SKILL.md and optional references.");
}
