namespace Circuit.FSharp.Tests.DocumentationExamples

open System.Collections.Generic
open Circuit.Core

module SkillsSecurityExample =
    let inlineSkill =
        SkillReference.Create(
            "skill.inline-style",
            "1.0.0",
            "Inline support guidance.",
            SkillSource.CreateInline(
                "Use a calm, concise support tone.",
                [ SkillResource.Create("glossary", box "vip = high-touch customer") ],
                [ SkillScriptDescriptor.Create("normalize-contact") ]
            )
        )

    let resolvedSkill =
        ResolvedSkill.Create(
            inlineSkill,
            [ KeyValuePair("audience", box "premium-support")
              KeyValuePair("revision", box 3) ]
        )

    let fileSkill =
        SkillReference.Create(
            "skill.file-style",
            "1.0.0",
            "File-backed guidance with SKILL.md and optional references.",
            SkillSource.CreateFile "/srv/circuit/skills/support-style"
        )
