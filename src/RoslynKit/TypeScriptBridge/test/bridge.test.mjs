import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { once } from "node:events";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { createInterface } from "node:readline";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { NativePreviewWorkspace } from "../bridge.mjs";

const bridgeRoot = resolve(fileURLToPath(new URL("..", import.meta.url)));
const packageRoot = resolve(bridgeRoot, "node_modules", "@typescript", "native-preview");
const fixtureRoot = resolve(bridgeRoot, "..", "..", "..", "tests", "TypeScriptFixture");
const configPath = resolve(fixtureRoot, "tsconfig.json");
const javaScriptConfigPath = resolve(bridgeRoot, "..", "..", "..", "tests", "JavaScriptFixture", "jsconfig.json");

test("native preview workspace reuses its API and snapshot across commands", async () => {
  const workspace = new NativePreviewWorkspace(configPath, packageRoot);
  try {
    const first = await workspace.execute("symbols", { query: "UserFormatter", exact: "true" });
    const second = await workspace.execute("references", { symbol: extractSelector(first.stdout), "max-results": "20" });

    assert.equal(first.exitCode, 0, first.stdout);
    assert.equal(second.exitCode, 0, second.stdout);
    assert.equal(first.state.apiInstanceId, second.state.apiInstanceId);
    assert.equal(first.state.nativeProcessId, second.state.nativeProcessId);
    assert.equal(first.state.snapshotId, second.state.snapshotId);
    assert.match(second.stdout, /src\/usage\.tsx/);
  }
  finally {
    workspace.close();
  }
});

test("refresh replaces the snapshot but preserves the native compiler process", async () => {
  const workspace = new NativePreviewWorkspace(configPath, packageRoot);
  try {
    const before = await workspace.execute("workspace", {});
    const refreshed = await workspace.refresh();
    const after = await workspace.execute("workspace", {});

    assert.equal(before.exitCode, 0, before.stdout);
    assert.equal(after.exitCode, 0, after.stdout);
    assert.equal(before.state.apiInstanceId, after.state.apiInstanceId);
    assert.equal(before.state.nativeProcessId, after.state.nativeProcessId);
    assert.notEqual(before.state.snapshotId, refreshed.snapshotId);
    assert.equal(after.state.snapshotId, refreshed.snapshotId);
  }
  finally {
    workspace.close();
  }
});

test("workspace covers TypeScript, TSX, and JavaScript and reports diagnostics", async () => {
  const workspace = new NativePreviewWorkspace(configPath, packageRoot);
  try {
    const workspaceResult = await workspace.execute("workspace", {});
    const diagnostics = await workspace.execute("diagnostics", { "max-results": "50" });
    const corpus = await workspace.execute("corpus", {});

    assert.match(workspaceResult.stdout, /src\/formatters\.ts/);
    assert.match(workspaceResult.stdout, /src\/usage\.tsx/);
    assert.match(workspaceResult.stdout, /src\/javascript\.js/);
    assert.match(workspaceResult.stdout, /src\/module\.mts/);
    assert.match(workspaceResult.stdout, /src\/common\.cts/);
    assert.match(workspaceResult.stdout, /src\/component\.jsx/);
    assert.match(workspaceResult.stdout, /src\/utility\.mjs/);
    assert.match(workspaceResult.stdout, /src\/legacy\.cjs/);
    assert.match(diagnostics.stdout, /TS2322/);
    assert.ok(corpus.stdout.records.some(record => record.displayName.includes("UserFormatter")));
    assert.ok(corpus.stdout.records.every(record => record.selector.startsWith("ts:")));
  }
  finally {
    workspace.close();
  }
});

test("native semantics cover declarations, navigation, implementations, overloads, and generics", async () => {
  const workspace = new NativePreviewWorkspace(configPath, packageRoot);
  try {
    const symbols = await workspace.execute("symbols", { query: "UserFormatter", exact: "true" });
    const caseInsensitive = await workspace.execute("symbols", { query: "userformatter", exact: "true" });
    const caseSensitive = await workspace.execute("symbols", {
      query: "userformatter", exact: "true", "case-sensitive": "true",
    });
    const selector = extractSelector(symbols.stdout);
    const documentText = await workspace.execute("document-text", { file: resolve(fixtureRoot, "src", "formatters.ts") });
    const documentLines = await workspace.execute("document-lines", {
      file: resolve(fixtureRoot, "src", "formatters.ts"),
      "start-line": "3",
      "end-line": "10",
    });
    const documentSymbols = await workspace.execute("document-symbols", { file: resolve(fixtureRoot, "src", "formatters.ts") });
    const definition = await workspace.execute("definition", { symbol: selector });
    const positionalDefinition = await workspace.execute("definition", {
      file: resolve(fixtureRoot, "src", "usage.tsx"), line: "3", column: "24",
    });
    const typeDefinition = await workspace.execute("type-definition", {
      file: resolve(fixtureRoot, "src", "usage.tsx"), line: "4", column: "13",
    });
    const references = await workspace.execute("references", { symbol: selector, "max-results": "20" });
    const implementations = await workspace.execute("implementations", {
      file: resolve(fixtureRoot, "src", "contracts.ts"), line: "2", column: "18",
    });
    const quickInfo = await workspace.execute("quick-info", {
      file: resolve(fixtureRoot, "src", "usage.tsx"), line: "4", column: "20",
    });
    const signatureHelp = await workspace.execute("signature-help", {
      file: resolve(fixtureRoot, "src", "usage.tsx"), line: "5", column: "53",
    });
    const symbolSource = await workspace.execute("symbol-source", { symbol: selector });

    for (const result of [symbols, caseInsensitive, caseSensitive, documentText, documentLines, documentSymbols, definition,
      positionalDefinition, typeDefinition, references, implementations, quickInfo,
      signatureHelp, symbolSource]) {
      assert.equal(result.exitCode, 0, result.stdout);
    }
    assert.match(documentText.stdout, /identity<T>/);
    assert.match(caseInsensitive.stdout, /UserFormatter/);
    assert.match(caseSensitive.stdout, /returned: 0\/0/);
    assert.match(documentLines.stdout, /format\(value: User, prefix: string\)/);
    assert.match(documentSymbols.stdout, /kind: Interface name: `User`/);
    assert.match(definition.stdout, /UserFormatter/);
    assert.match(positionalDefinition.stdout, /src\/formatters\.ts/);
    assert.match(typeDefinition.stdout, /kind: Interface name: `User`/);
    assert.match(references.stdout, /src\/usage\.tsx/);
    assert.match(implementations.stdout, /FormatterBase/);
    assert.match(implementations.stdout, /UserFormatter/);
    assert.match(quickInfo.stdout, /<T>\(value: T\) => T/);
    assert.match(signatureHelp.stdout, /format\(value: User, prefix: string\): string/);
    assert.match(symbolSource.stdout, /export class UserFormatter/);
  }
  finally {
    workspace.close();
  }
});

test("refresh observes source changes while preserving the API and native process", async () => {
  const temporaryRoot = mkdtempSync(resolve(tmpdir(), "roslynkit-native-preview-"));
  cpSync(fixtureRoot, temporaryRoot, { recursive: true });
  const temporaryConfig = resolve(temporaryRoot, "tsconfig.json");
  const sourcePath = resolve(temporaryRoot, "src", "formatters.ts");
  const workspace = new NativePreviewWorkspace(temporaryConfig, packageRoot);
  try {
    const before = await workspace.execute("symbols", { query: "RefreshedFormatter", exact: "true" });
    writeFileSync(sourcePath, `${readFileSync(sourcePath, "utf8")}\nexport class RefreshedFormatter {}\n`);
    const refreshed = await workspace.refresh();
    const after = await workspace.execute("symbols", { query: "RefreshedFormatter", exact: "true" });

    assert.match(before.stdout, /returned: 0\/0/);
    assert.match(after.stdout, /RefreshedFormatter/);
    assert.equal(before.state.apiInstanceId, after.state.apiInstanceId);
    assert.equal(before.state.nativeProcessId, after.state.nativeProcessId);
    assert.notEqual(before.state.snapshotId, refreshed.snapshotId);
  }
  finally {
    workspace.close();
    rmSync(temporaryRoot, { recursive: true, force: true });
  }
});

test("jsconfig projects load CommonJS and ESM JavaScript", async () => {
  const workspace = new NativePreviewWorkspace(javaScriptConfigPath, packageRoot);
  try {
    const project = await workspace.execute("workspace", {});
    const symbols = await workspace.execute("symbols", { query: "greet" });
    const diagnostics = await workspace.execute("diagnostics", { "max-results": "20" });

    assert.equal(project.exitCode, 0, project.stdout);
    assert.match(project.stdout, /src\/greeter\.cjs/);
    assert.match(project.stdout, /src\/module\.mjs/);
    assert.equal(symbols.exitCode, 0, symbols.stdout);
    assert.match(symbols.stdout, /greet/);
    assert.equal(diagnostics.exitCode, 0, diagnostics.stdout);
  }
  finally {
    workspace.close();
  }
});

test("JSON-lines bridge keeps one native process for repeated requests", async () => {
  const child = spawn(process.execPath, [resolve(bridgeRoot, "bridge.mjs"), "--config", configPath,
    "--native-preview-root", packageRoot], { stdio: ["pipe", "pipe", "pipe"] });
  const lines = createInterface({ input: child.stdout });
  const iterator = lines[Symbol.asyncIterator]();
  try {
    child.stdin.write(`${JSON.stringify({ id: 41, command: "workspace", options: {} })}\n`);
    const first = JSON.parse((await iterator.next()).value);
    child.stdin.write(`${JSON.stringify({ id: 42, command: "symbols", options: { query: "identity" } })}\n`);
    const second = JSON.parse((await iterator.next()).value);

    assert.equal(first.id, 41);
    assert.equal(second.id, 42);
    assert.equal(first.exitCode, 0, first.stdout);
    assert.equal(second.exitCode, 0, second.stdout);
    assert.equal(first.state.bridgeProcessId, second.state.bridgeProcessId);
    assert.equal(first.state.nativeProcessId, second.state.nativeProcessId);
    assert.equal(first.state.apiInstanceId, second.state.apiInstanceId);
    assert.equal(first.state.snapshotId, second.state.snapshotId);
    child.stdin.write(`${JSON.stringify({ id: 43, command: "close", options: {} })}\n`);
    const closed = JSON.parse((await iterator.next()).value);
    assert.equal(closed.id, 43);
    assert.equal(closed.exitCode, 0);
  }
  finally {
    lines.close();
    child.stdin.end();
    if (child.exitCode === null) await once(child, "exit");
  }
});

function extractSelector(markdown) {
  const match = markdown.match(/ id: `([^`]+)`/);
  assert.ok(match, markdown);
  return match[1];
}
