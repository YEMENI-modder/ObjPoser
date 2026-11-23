# Contributing

Contributions are welcome (code, poses, bug reports with a sample model).

1. Read [`AGENTS.md`](AGENTS.md): it explains the architecture, the conventions (coordinate system, joint frames, pose format) and how to test.
2. Keep the project dependency-free (only the bundled MIT libraries) and compilable with the classic C# compiler / Mono `mcs` (C# 5 style: no `?.`, no string interpolation, no tuples).
3. Run the self-test before opening a pull request:
   ```
   csc /out:selftest.exe tests\CoreSelfTest.cs src\Core.cs src\Skeleton.cs src\Pose.cs src\Skinner.cs src\Export.cs
   selftest.exe samples\sample_tpose.obj
   ```
4. Do not commit game assets or models you do not have the right to share.

Note: the original code base was written entirely by AI (Claude Sonnet 5.5). Please keep the credits in `README.md` / the About dialog accurate when you change them.
