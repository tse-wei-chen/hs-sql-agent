import { pathToFileURL } from "node:url";

/**
 * Fail closed on unknown release-tag formats: never silently omit something
 * that a maintainer might believe was deleted.
 */
export function classifyReleaseTag(tag) {
  const match = /^v?(\d+)\.(\d+)\.(\d+)(?:[-+][0-9A-Za-z.+-]+)?$/.exec(tag);
  if (!match) throw new Error(`Unrecognized release tag "${tag}"; aborting.`);
  return Number(match[1]) < 3 ? "retire" : "retain";
}

export function selectPreV3Releases(releases, expectedCount) {
  if (!Number.isSafeInteger(expectedCount) || expectedCount < 0) {
    throw new Error("expected_count must be a non-negative integer.");
  }
  const seenIds = new Set();
  const targets = [];
  for (const release of releases) {
    if (!Number.isSafeInteger(release.id) || release.id <= 0 || seenIds.has(release.id)) {
      throw new Error("Invalid or duplicate GitHub release ID.");
    }
    seenIds.add(release.id);
    if (classifyReleaseTag(release.tag_name) !== "retire") continue;
    if (release.immutable === true) {
      throw new Error(`Release ${release.tag_name} is immutable; refusing partial cleanup.`);
    }
    targets.push(release);
  }
  if (targets.length !== expectedCount) {
    throw new Error(`Expected ${expectedCount} pre-v3 releases, but found ${targets.length}; nothing deleted.`);
  }
  return targets;
}

async function githubApi(path, method = "GET") {
  const response = await fetch(`https://api.github.com${path}`, {
    method,
    headers: {
      Authorization: `Bearer ${process.env.GH_TOKEN}`,
      Accept: "application/vnd.github+json",
      "X-GitHub-Api-Version": "2022-11-28",
      "User-Agent": "hs-sql-agent-release-prune",
    },
  });
  if (!response.ok) {
    const detail = (await response.text()).slice(0, 500);
    throw new Error(`GitHub ${method} ${path} returned HTTP ${response.status}: ${detail}`);
  }
  return response.status === 204 ? undefined : response.json();
}

async function listReleases(repository) {
  const all = [];
  for (let page = 1; ; page += 1) {
    const pageReleases = await githubApi(
      `/repos/${repository}/releases?per_page=100&page=${page}`
    );
    if (!Array.isArray(pageReleases)) throw new Error("Expected a release array.");
    all.push(...pageReleases);
    if (pageReleases.length < 100) return all;
    if (page >= 100) throw new Error("Release pagination exceeded safety bound.");
  }
}

export async function main() {
  const repository = process.env.GITHUB_REPOSITORY;
  const dryRun = process.env.DRY_RUN !== "false";
  const expectedCount = Number(process.env.EXPECTED_COUNT || "49");

  if (repository !== "tse-wei-chen/hs-sql-agent") {
    throw new Error("This script is restricted to tse-wei-chen/hs-sql-agent.");
  }
  if (!process.env.GH_TOKEN) throw new Error("GH_TOKEN is missing.");
  if (process.env.GITHUB_REF !== "refs/heads/main") {
    throw new Error("Only the main branch may run the release cleanup.");
  }

  const allReleases = await listReleases(repository);
  const targets = selectPreV3Releases(allReleases, expectedCount);
  console.log(`Found ${allReleases.length} releases; ${targets.length} pre-v3 releases selected.`);
  for (const release of targets) {
    console.log(`  ${release.tag_name} (release id ${release.id}, assets ${release.assets?.length ?? "unknown"})`);
  }
  console.log("v3.0.0+ release records and all Git tags remain untouched.");

  if (dryRun) {
    console.log("DRY RUN ONLY — no releases deleted.");
    return;
  }
  if (process.env.CONFIRMATION !== "DELETE PRE-V3 RELEASES") {
    throw new Error("Set confirmation to DELETE PRE-V3 RELEASES to authorize deletion.");
  }
  for (const release of targets) {
    await githubApi(`/repos/${repository}/releases/${release.id}`, "DELETE");
    console.log(`Deleted release ${release.tag_name} (${release.id}).`);
  }
  console.log(`Deleted ${targets.length} pre-v3 GitHub Release records. Git tags preserved.`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch(error => {
    console.error(error);
    process.exitCode = 1;
  });
}
