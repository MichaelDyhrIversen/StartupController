# Contributing

Thank you for your interest in contributing to this project. This document explains the simplest, most predictable way to contribute code, fixes, tests, and documentation.

## Table of contents
- Need help or want to ask something?
- Code of conduct
- License and contributor agreement
- Reporting bugs
- Requesting features
- Contributing code (recommended workflow)
- Pull request guidelines
- Commit message style
- Tests & continuous integration
- Documentation
- Small/quick contributions
- Security issues
- Contact

## Need help or want to ask something?
Open an issue describing your question, or start a discussion if the repository has GitHub Discussions enabled. Use a clear title and enough context to reproduce or explain your problem.

## Code of conduct
Be respectful, constructive, and inclusive. This project follows the repository's CODE_OF_CONDUCT.md (if present). Unacceptable behavior may result in removal from the community.

## License and contributor agreement
This project is released under the GNU General Public License v3 (GPLv3). By submitting a contribution (issue, pull request, or other), you agree to license your contribution under GPLv3 as well so it can be distributed under the project license.

If your contribution includes third-party code or a license that conflicts with GPLv3, please disclose it in your PR and contact the maintainers before opening the PR.

## Reporting bugs
Before opening a bug report:
- Search existing issues to avoid duplicates.
- Reproduce the problem with the latest main branch or latest release tag.

When reporting a bug, include:
- A short, descriptive title.
- Steps to reproduce (minimal reproduction is best).
- Expected behavior and actual behavior.
- Project version (release tag or commit SHA).
- Environment: OS, .NET / .NET Core / Mono version, relevant SDK/runtime versions.
- Error messages, stack traces, and any relevant logs.
- A small code sample or repository showing the issue, if possible.

Example:
- Title: NullReferenceException when calling Foo.Run on .NET 6
- Steps:
  1. Build with `dotnet build`
  2. Run `dotnet run --project samples/Example`
- Expected: Program runs without exception
- Actual: NullReferenceException in Foo.cs:42
- Environment: Windows 10, .NET 6.0.403

## Requesting features
Create an issue describing:
- The problem or gap that motivates the feature.
- How users would use the feature (example usage or API).
- Any alternatives you considered.

Maintainers may ask clarifying questions or propose simpler approaches. If you start implementing the feature, link the issue from your PR (e.g., `Closes #123`).

## Contributing code (recommended workflow)
Preferred branch: main (replace with `master` if your repo uses master).

1. Fork the repository.
2. Clone your fork:
   ```bash
   git clone https://github.com/<your-username>/<repo>.git
   cd <repo>

3. Add upstream remote (first time only):
    ```bash
    git remote add upstream https://github.com/<project-owner>/<repo>.git```
4. Create a new branch with a descriptive name:
    ```bash
    git checkout -b feat/brief-description
    # or
    git checkout -b fix/brief-description
5. Make small, focused commits. Prefer one logical change per branch/PR.
6. Run and update tests locally (see Tests section).
7. Rebase or merge frequently to keep your branch up to date:
    ```bash
    git fetch upstream
    git rebase upstream/main
    # or
    git merge upstream/main
8. Push your branch and open a Pull Request against the repository's main branch:
    ```bash
    git push origin HEAD

On GitHub, open a PR, include a clear title and description, and reference related issues (e.g., Closes #123).

## Pull request guidelines
Keep PRs focused and small when possible.
Include a clear description of what you changed and why.
Reference related issues and link tests or example projects that demonstrate the change.
Add or update unit tests for bug fixes and new features.
Ensure the project builds and tests pass in CI.
Be responsive to review comments and update the PR as requested.
Maintainers may squash or rebase commits when merging. If you prefer a clean history, consider rebasing/squashing before creating the PR.
## Commit message style
Use short, meaningful commit messages. Suggested format:

Subject line (50 chars max): type(scope): short description
Optional body: more context and rationale.
Common types:

feat: new feature
fix: bug fix
docs: documentation only changes
test: adding or updating tests
chore: maintenance tasks (build, tooling, deps)
Example:

    fix(parser): handle empty input in ParseItems

## Tests & continuous integration
This is a C# project. Use dotnet commands to build and test:
Build: dotnet build
Run tests: dotnet test
Add tests for bug fixes and new features. Tests should be deterministic and fast where possible.
Ensure CI (GitHub Actions or other) passes before requesting a final review.
If you cannot run tests locally, explain in the PR how you validated the change.

## Documentation
Update README.md and any documentation when changing public APIs or behavior.
Add usage examples for new features.
Small documentation fixes (typos, clarifications) are welcome as direct PRs.

## Small / quick contributions
For typo fixes, small doc updates, or minor code cleanups:

You can use the GitHub web editor or open a small branch and create a PR.
Keep your changes focused and include a brief description.

## Security issues
Do not disclose security vulnerabilities in public issues. Instead:

Check for a SECURITY.md file with disclosure instructions.

## Thank you for helping improve the project!
