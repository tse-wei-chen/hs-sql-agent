import assert from "node:assert/strict";
import test from "node:test";
import { classifyReleaseTag, selectPreV3Releases } from "./prune-pre-v3-releases.mjs";

const release = (id, tag_name, immutable = false) => ({
  id,
  tag_name,
  immutable,
  assets: [],
});

test("retire historical v1 and v2, including alpha tags without v prefix", () => {
  for (const tag of ["v1.10.7", "v2.0.6", "v1.8.2-alpha", "1.4.0-alpha"]) {
    assert.equal(classifyReleaseTag(tag), "retire");
  }
});

test("preserve v3 and later, including pre-releases", () => {
  for (const tag of ["v3.0.0", "v3.1.0", "v3.2.0-rc.1", "v4.0.0"]) {
    assert.equal(classifyReleaseTag(tag), "retain");
  }
});

test("reject unexpected release tags instead of guessing", () => {
  assert.throws(() => classifyReleaseTag("current"), /Unrecognized/);
  assert.throws(() => classifyReleaseTag("2.x"), /Unrecognized/);
});

test("select only pre-v3 release records and enforce preflight count", () => {
  const data = [release(1, "v3.1.0"), release(2, "v3.0.0"), release(3, "v2.0.6"), release(4, "v1.10.7")];
  assert.deepEqual(selectPreV3Releases(data, 2).map(item => item.id), [3, 4]);
  assert.throws(() => selectPreV3Releases(data, 49), /Expected 49/);
});

test("refuse duplicate IDs, invalid counts, and immutable historical releases", () => {
  assert.throws(() => selectPreV3Releases([release(1, "v2.0.6"), release(1, "v2.0.5")], 2), /duplicate/);
  assert.throws(() => selectPreV3Releases([release(2, "v2.0.6", true)], 1), /immutable/);
  assert.throws(() => selectPreV3Releases([], -1), /non-negative/);
});
