// Bound task-specific prompt transport for the existing isolated ACP coding turn.
// The legacy M1.12 fixture mode remains the default. This module never reads
// host source paths, enables tools, chooses a model, or authorizes execution.
import { createHash, timingSafeEqual } from "node:crypto";
import { lstatSync, readFileSync } from "node:fs";

export const CONTRACT_PATH = "/run/codingassistant-task/task.json";
export const CONTRACT_SCHEMA = "codingassistant-task-contract-v1";
const MAX_BYTES = 24576;
const MODEL = "qwen3.5:4b";
const KEYSET = [
  "schema", "taskId", "specificationVersion", "approvedSpecificationSha256",
  "specificationSha256",
  "objective", "specification", "model", "files",
].sort().join("|");
const EXTENSIONS = new Set([
  "cs", "csproj", "ts", "tsx", "js", "jsx", "css", "html", "json",
  "md", "txt", "py", "sln", "yml", "yaml",
]);

function requireValid(value) {
  if (!value) throw new Error("Task contract rejected");
}
function safeFile(path) {
  requireValid(typeof path === "string" && path.length >= 3 && path.length <= 140);
  requireValid(!path.includes("\\") && !path.includes(":") && !path.includes("//"));
  const parts = path.split("/");
  requireValid(parts.length <= 4 && parts.every(part =>
    /^[A-Za-z0-9][A-Za-z0-9_.-]{0,70}$/.test(part) &&
    part !== "." && part !== ".." && !part.startsWith(".") &&
    !part.endsWith(".") &&
    !["node_modules", "bin", "obj", "artifacts"].includes(part.toLowerCase())));
  requireValid(EXTENSIONS.has(path.split(".").at(-1).toLowerCase()));
  return path;
}

export function validateTaskContract(bytes, expectedSha256) {
  requireValid(Buffer.isBuffer(bytes) && bytes.length > 0 && bytes.length <= MAX_BYTES);
  requireValid(typeof expectedSha256 === "string" && /^[a-f0-9]{64}$/.test(expectedSha256));
  const actual = createHash("sha256").update(bytes).digest();
  requireValid(timingSafeEqual(actual, Buffer.from(expectedSha256, "hex")));
  const source = bytes.toString("utf8");
  requireValid(!source.includes("\uFFFD") && !source.includes("\u0000"));
  let value;
  try { value = JSON.parse(source); }
  catch { throw new Error("Task contract rejected"); }
  requireValid(value && typeof value === "object" && !Array.isArray(value));
  requireValid(Object.keys(value).sort().join("|") === KEYSET);
  requireValid(value.schema === CONTRACT_SCHEMA && value.model === MODEL);
  requireValid(typeof value.taskId === "string" && /^[a-f0-9]{32}$/.test(value.taskId));
  requireValid(Number.isSafeInteger(value.specificationVersion) &&
               value.specificationVersion >= 1 && value.specificationVersion <= 1_000_000);
  requireValid(typeof value.objective === "string" &&
               value.objective.trim().length >= 8 && value.objective.length <= 4000);
  requireValid(typeof value.specification === "string" &&
               value.specification.trim().length >= 32 && value.specification.length <= 16000);
  requireValid(typeof value.approvedSpecificationSha256 === "string" &&
               /^[a-f0-9]{64}$/.test(value.approvedSpecificationSha256));
  requireValid(typeof value.specificationSha256 === "string" &&
               /^[a-f0-9]{64}$/.test(value.specificationSha256));
  const specSha = createHash("sha256").update(value.specification, "utf8").digest("hex");
  requireValid(specSha === value.specificationSha256);
  requireValid(Array.isArray(value.files) && value.files.length >= 1 && value.files.length <= 5);
  const names = value.files.map(safeFile);
  requireValid(new Set(names.map(name => name.toLowerCase())).size === names.length);
  return Object.freeze({ ...value, files: Object.freeze(names) });
}

export function loadTaskContract() {
  const hash = process.env.CA_M1_TASK_CONTRACT_SHA256;
  if (hash === undefined) return null; // Existing frozen M1.12 contract unchanged.
  const info = lstatSync(CONTRACT_PATH);
  requireValid(info.isFile() && !info.isSymbolicLink());
  return validateTaskContract(readFileSync(CONTRACT_PATH), hash);
}

export function taskPrompt(contract) {
  const paths = contract.files.map(name => "- " + name).join("\n");
  return [
    "You are OpenCode inside a fresh, disposable, isolated /workspace.",
    "Implement the owner's approved software specification, not a sample fixture.",
    "Create or edit only these project-relative files (no others):\n" + paths,
    "Do not access other directories, credentials, Git, network, shell commands,",
    "package managers, or external services. A separate trusted host will compile",
    "and test what you create. Never claim tests have run.",
    "OWNER OBJECTIVE:\n" + contract.objective,
    "APPROVED SPECIFICATION:\n" + contract.specification,
    "Produce the requested source files with correct project structure.",
  ].join("\n\n");
}

export function taskPermissions(contract) {
  const permissions = [{ action: "*", resource: "*", effect: "deny" }];
  for (const path of contract.files) {
    for (const resource of [path, "/workspace/" + path]) {
      permissions.push({ action: "read", resource, effect: "allow" });
      permissions.push({ action: "edit", resource, effect: "allow" });
    }
  }
  return permissions;
}
