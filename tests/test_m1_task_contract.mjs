// Model-free task contract and agent-permission security regression tests.
import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import {
  validateTaskContract, taskPrompt, taskPermissions, loadTaskContract,
  CONTRACT_SCHEMA,
} from "../infra/m1/coding-plugin/task-contract.mjs";

function hash(data) { return createHash("sha256").update(data).digest("hex"); }
function valid() {
  const specification =
    "Build a .NET 10 API with an expense model, CRUD endpoints and tests.\n" +
    "The API must not reach any cloud services. Use a local SQLite store.";
  return {
    schema: CONTRACT_SCHEMA, taskId: "a".repeat(32),
    specificationVersion: 1, specificationSha256: hash(specification),
    objective: "Implement a local expense tracking API",
    specification, model: "qwen3.5:4b",
    files: ["ExpenseApi/ExpenseApi.csproj", "ExpenseApi/Program.cs"],
  };
}
function validate(item) {
  const raw = Buffer.from(JSON.stringify(item), "utf8");
  return validateTaskContract(raw, hash(raw));
}
test("no task contract retains original frozen fixture mode", () => {
  const was = process.env.CA_M1_TASK_CONTRACT_SHA256;
  try {
    delete process.env.CA_M1_TASK_CONTRACT_SHA256;
    assert.equal(loadTaskContract(), null);
  } finally {
    if (was !== undefined) process.env.CA_M1_TASK_CONTRACT_SHA256 = was;
  }
});
test("valid approved specification is byte-bound and drives actual task prompt", () => {
  const contract = validate(valid());
  const prompt = taskPrompt(contract);
  assert.match(prompt, /local expense tracking API/);
  assert.match(prompt, /ExpenseApi\\/Program.cs/);
  assert.match(prompt, /approved software specification/);
  assert.match(prompt, /Never claim tests have run/);
  assert.doesNotMatch(prompt, /Fix the compiler errors in api\\/Program.cs/);
});
test("file operations are explicitly restricted to approved paths", () => {
  const paths = valid().files;
  const permissions = taskPermissions(validate(valid()));
  assert.deepEqual(permissions[0],
    { action: "*", resource: "*", effect: "deny" });
  for (const p of paths) {
    assert(permissions.some(rule => rule.action === "edit" &&
        rule.resource === "/workspace/" + p && rule.effect === "allow"));
    assert(permissions.some(rule => rule.action === "read" &&
        rule.resource === p && rule.effect === "allow"));
  }
  assert(!permissions.some(rule => rule.effect === "allow" &&
      ["bash", "shell", "exec", "provider.use"].includes(rule.action)));
  assert(!permissions.some(rule => rule.effect === "allow" &&
      ["/etc/passwd", "/workspace/.git/config"].includes(rule.resource)));
});
test("tampering with bytes or SHA-256 fails closed", () => {
  const raw = Buffer.from(JSON.stringify(valid()));
  assert.throws(() => validateTaskContract(raw, "f".repeat(64)));
  assert.throws(() => validateTaskContract(raw, "not-a-sha"));
  assert.throws(() => validateTaskContract(Buffer.concat([raw, Buffer.from(" ")]), hash(raw)));
  assert.throws(() => validateTaskContract(Buffer.alloc(24577), hash(Buffer.alloc(24577))));
});
test("rejects malformed input and contract downgrade", () => {
  const mutations = [
    { schema: "codingassistant-task-contract-v0" },
    { taskId: "broken" },
    { model: "remote/cloud" },
    { specificationVersion: 0 },
    { specificationVersion: 2.5 },
    { specificationSha256: "0".repeat(64) },
    { objective: "x" },
    { specification: "short" },
    { files: [] },
    { files: Array(6).fill("src/App.cs") },
    { files: ["foo.cs", "FOO.cs"] },
    { files: ["../etc/passwd"] },
    { files: ["/tmp/test.cs"] },
    { files: ["src/../../outside.cs"] },
    { files: ["src\\\\evil.cs"] },
    { files: [".git/config"] },
    { files: ["bin/program.cs"] },
    { files: ["src/new.exe"] },
    { files: ["src/file.cs", "src/file.cs"] },
  ];
  for (const changes of mutations) {
    const item = { ...valid(), ...changes };
    assert.throws(() => validate(item), JSON.stringify(changes));
  }
  assert.throws(() => validate({ ...valid(), extra: "unexpected" }));
});
test("specification text changes require a new exact specification hash", () => {
  const item = valid();
  item.specification += "\nAnother requirement";
  assert.throws(() => validate(item));
  item.specificationSha256 = hash(item.specification);
  assert.equal(validate(item).specification, item.specification);
});
