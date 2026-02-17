---
description: Recommends remote servicing branches to remove based on the versions of Visual Studio still supported.
---

Your task is to help identify servicing branches that are no longer needed.

## Steps

Follow the steps in each subsection, in the order presented.

### Freshen up

Run `git fetch --prune` to make sure you're operating with current remote branch information.

### Find candidates for deletion

Enumerate the git remote branches whose names match a `vX.Y` pattern and determine which of them track a version of the library that ships in versions of Visual Studio that are no longer supported.

These MAY be candidates for deletion, but we have to verify.

It may be that a given remote branch builds a version that ships in *multiple* Visual Studio versions.
If there is no entry in the [vs.md](../../docfx/docs/vs.md) file for a particular VS version, then that (missing) VS version uses the same version of this library that the last VS version in the table specifies.
So for example StreamJsonRpc 2.8 first shipped with VS 2019.10 as we see from the table.
VS 2019.11 is not documented in that table, so it ships StreamJsonRpc 2.8 as well.
Thus, if VS 2019.10 is no longer supported but VS 2019.10 _is_ still supported, then we must keep the `v2.8` branch and it is _not_ a candidate for deletion.

For the VS 2026 major version (and any later), only the currently developed VS version within that major version and its two immediately preceding minor versions are supported.
If you see a 2026.3 VS version in the mapping table, that means that the .3, .2, and .1 minor versions are supported and 2026.0 is no longer supported.

When you see VS 2027 in the mapping table, that means only VS 2026's last minor release (whatever that is) is supported instead of the last 3 minor releases.

### Verify merge status

For each deletion candidate, verify that it has already merged into a newer branch.
For example if the `v2.19` branch is a candidate for deletion, determine whether it has already merged into `v2.20` or whatever the next newer branch is.

### Report your findings

Share a table with the user showing each remote branch, at least one VS version that is still supported that ships the version from that remote branch, and whether that branch has already been merged with a newer branch.

For example, you might construct the following table:

StreamJsonRpc | Visual Studio   | Merged?
--------------|-----------------|--------
v1.5          | 16.11           | N/A
v2.7          | **unsupported** | ✅
v2.8          | 2019.11         | N/A
v2.23         | 2026.0          | ❌

If no remote branches are candidates for deletion, simply inform the user. You're done.

### Help the user with next steps

If there are branches that are candidates for deletion, proceed to help the user with the following:

Offer a `git push` command that will delete the remote branches that _have_ already been merged.

Offer to create pull requests for the branches that have not yet been merged.
These need to be carefully created, since we don't want to disrupt newer branches with changes from older branches that might not apply.

Follow these steps to prepare pull requests for the branches that need it:

1. Create new topic branches that merge older branch X into the next newer branch Y.
   Resolve any conflicts.
2. Validate the merge by running `dotnet build` in the repo root and then running `tools/dotnet-test-cloud.ps1`.
3. Simple failures may be fixed and committed to the same topic branch.
   If the failures are significant, consider replacing your 'real' merge from X to Y with a no-op merge so that the commits in X are 'saved' in history of Y, but Y does not take any of the source changes unique to X.
4. Create a pull request from the topic branch to the target branch Y.

When Y itself is also a candidate for deletion, it is important that X merge into Y first, and that Y *then* merge into Z (the next newer branch) so that X's unique commits are saved in Z.
This cascading merge repeats until all branches that are candidates for deletion have been saved in a branch that will survive.

## Resources

- The [vs.md](../../docfx/docs/vs.md) file maps the StreamJsonRpc versions to the corresponding Visual Studio versions.
- The Visual Studio versions still under service are documented at these locations:
  - [Visual Studio 2019](https://learn.microsoft.com/lifecycle/products/visual-studio-2019)
  - [Visual Studio 2022](https://learn.microsoft.com/lifecycle/products/visual-studio-2022)
  - [Visual Studio 2026](https://learn.microsoft.com/lifecycle/products/visual-studio-2026)
  - There may be even newer versions of Visual Studio than are listed here.
    You can [find them here](https://learn.microsoft.com/lifecycle/products/?products=vs).
    Only pay attention to the regular Visual Studio for Windows product versions (not Visual Studio Code, HockeyApp, Visual Studio Team Foundation Server, etc.)
