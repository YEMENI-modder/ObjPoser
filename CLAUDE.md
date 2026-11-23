# CLAUDE.md

All project documentation for AI assistants lives in **[AGENTS.md](AGENTS.md)**: architecture, coordinate / pose conventions, build and test commands, and how to extend the project. Read it before changing anything.

Quick reminders:
- C# 5-compatible syntax only (the project is also built with Mono `mcs`); no new dependencies.
- Run `tests/CoreSelfTest.cs` after changes to the core files (`Core`, `Skeleton`, `Pose`, `Skinner`, `Export`).
- Refer to joints by name (`Skeleton.Idx("L UpperArm")`), never by index.
- This code base was written by Claude Sonnet 5.5 for the owner YEMENI-modder; keep the credits accurate.
