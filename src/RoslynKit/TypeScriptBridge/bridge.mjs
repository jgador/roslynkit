import { createHash } from "node:crypto";
import { createRequire } from "node:module";
import { createInterface } from "node:readline";
import { basename, dirname, extname, isAbsolute, relative, resolve, sep } from "node:path";
import { pathToFileURL } from "node:url";

const supportedExtensions = new Set([".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs"]);
const declarationKinds = new Map([
  ["ClassDeclaration", "class"],
  ["ClassExpression", "class"],
  ["InterfaceDeclaration", "interface"],
  ["FunctionDeclaration", "method"],
  ["FunctionExpression", "method"],
  ["ArrowFunction", "method"],
  ["MethodDeclaration", "method"],
  ["MethodSignature", "method"],
  ["Constructor", "method"],
  ["GetAccessor", "property"],
  ["SetAccessor", "property"],
  ["PropertyDeclaration", "property"],
  ["PropertySignature", "property"],
  ["VariableDeclaration", "field"],
  ["Parameter", "field"],
  ["TypeAliasDeclaration", "type"],
  ["EnumDeclaration", "enum"],
  ["EnumMember", "field"],
  ["ModuleDeclaration", "namespace"],
]);

class UsageError extends Error {}

export class NativePreviewWorkspace {
  constructor(configPath, nativePreviewRoot) {
    this.configPath = resolve(configPath);
    this.workspaceRoot = dirname(this.configPath);
    this.nativePreviewRoot = resolve(nativePreviewRoot);
    this.api = undefined;
    this.snapshot = undefined;
    this.project = undefined;
    this.modules = undefined;
    this.refreshCount = 0;
    this.commandCount = 0;
    this.apiInstanceId = `${process.pid}-${Date.now()}`;
  }

  async initialize() {
    if (this.api) return;
    this.modules = await loadNativePreview(this.nativePreviewRoot);
    this.api = new this.modules.sync.API({ cwd: this.workspaceRoot });
    const snapshot = this.api.updateSnapshot({ openProjects: [this.configPath] });
    this.replaceSnapshot(snapshot);
  }

  async refresh() {
    await this.initialize();
    const snapshot = this.api.updateSnapshot({ fileChanges: { invalidateAll: true } });
    this.replaceSnapshot(snapshot);
    this.refreshCount++;
    return this.debugState();
  }

  replaceSnapshot(snapshot) {
    const project = snapshot.getProject(this.configPath)
      ?? snapshot.getProjects().find(candidate => resolve(candidate.configFileName) === this.configPath)
      ?? snapshot.getProjects()[0];
    if (!project) {
      snapshot.dispose();
      throw new Error(`The native TypeScript API did not load a project for '${this.configPath}'.`);
    }

    const previous = this.snapshot;
    this.snapshot = snapshot;
    this.project = project;
    previous?.dispose();
  }

  close() {
    this.snapshot?.dispose();
    this.snapshot = undefined;
    this.project = undefined;
    this.api?.close();
    this.api = undefined;
  }

  debugState() {
    const nativeProcessId = this.api?.client?.channel?.child?.pid ?? null;
    return {
      bridgeProcessId: process.pid,
      nativeProcessId,
      apiInstanceId: this.apiInstanceId,
      snapshotId: this.snapshot?.id ?? null,
      refreshCount: this.refreshCount,
      commandCount: this.commandCount,
      configPath: this.configPath,
    };
  }

  async execute(command, options = {}) {
    await this.initialize();
    this.commandCount++;
    try {
      const markdown = this.executeCore(command, options);
      return { exitCode: 0, stdout: markdown, stderr: "", state: this.debugState() };
    }
    catch (error) {
      const usage = error instanceof UsageError;
      const message = error instanceof Error ? error.message : String(error);
      return {
        exitCode: usage ? 2 : 1,
        stdout: `error: ${usage ? "usage" : error?.constructor?.name ?? "Error"}\nmessage: ${singleLine(message)}`,
        stderr: "",
        state: this.debugState(),
      };
    }
  }

  executeCore(command, options) {
    switch (command) {
      case "workspace": return this.renderWorkspace();
      case "diagnostics": return this.renderDiagnostics(options);
      case "symbols": return this.renderSymbols(options);
      case "document-text": return this.renderDocumentText(options);
      case "document-lines": return this.renderDocumentLines(options);
      case "document-symbols": return this.renderDocumentSymbols(options);
      case "definition": return this.renderDefinition(options);
      case "type-definition": return this.renderTypeDefinition(options);
      case "references": return this.renderReferences(options);
      case "implementations": return this.renderImplementations(options);
      case "quick-info": return this.renderQuickInfo(options);
      case "signature-help": return this.renderSignatureHelp(options);
      case "symbol-source": return this.renderSymbolSource(options);
      case "corpus": return this.buildCorpus();
      case "debug-state": return this.debugState();
      default: throw new UsageError(`The TypeScript backend does not support command '${command}'.`);
    }
  }

  get sourceFiles() {
    return this.project.program.getSourceFileNames()
      .map(fileName => this.project.program.getSourceFile(fileName))
      .filter(Boolean)
      .filter(sourceFile => this.isWorkspaceSource(sourceFile))
      .sort((left, right) => compare(this.displayPath(left.fileName), this.displayPath(right.fileName)));
  }

  isWorkspaceSource(sourceFile) {
    const extension = sourceExtension(sourceFile.fileName);
    if (!supportedExtensions.has(extension)) return false;
    if (!isWithin(this.workspaceRoot, sourceFile.fileName)) return false;
    if (this.project.program.isSourceFileDefaultLibrary(sourceFile)) return false;
    if (this.project.program.isSourceFileFromExternalLibrary(sourceFile)) return false;
    return true;
  }

  displayPath(fileName) {
    return normalizeSlashes(relative(this.workspaceRoot, resolve(fileName)));
  }

  documentDescriptor(sourceFile) {
    return {
      projectName: basename(this.configPath),
      projectPath: this.configPath,
      displayProjectPath: basename(this.configPath),
      name: basename(sourceFile.fileName),
      path: resolve(sourceFile.fileName),
      displayPath: this.displayPath(sourceFile.fileName),
      documentKind: "source",
    };
  }

  resolveFile(fileOption) {
    if (!fileOption) throw new UsageError("Missing required option '--file'.");
    const candidates = isAbsolute(fileOption)
      ? [resolve(fileOption)]
      : [
          resolve(process.env.ROSLYNKIT_INVOCATION_DIRECTORY ?? this.workspaceRoot, fileOption),
          resolve(this.workspaceRoot, fileOption),
        ];
    const sourceFile = this.sourceFiles.find(file => candidates.some(candidate => pathsEqual(file.fileName, candidate)));
    if (!sourceFile) {
      throw new UsageError(`File '${fileOption}' is not a supported source document in '${this.configPath}'.`);
    }
    return sourceFile;
  }

  position(sourceFile, lineText, columnText) {
    const line = positiveInteger(lineText, "line");
    const column = positiveInteger(columnText, "column");
    const lineStarts = sourceFile.getLineStarts();
    if (line > lineStarts.length) {
      throw new UsageError(`Line ${line} is outside the document range 1..${lineStarts.length}.`);
    }
    const lineStart = lineStarts[line - 1];
    const nextLineStart = line < lineStarts.length ? lineStarts[line] : sourceFile.text.length;
    const maximumColumn = Math.max(1, nextLineStart - lineStart + 1);
    if (column > maximumColumn) {
      throw new UsageError(`Column ${column} is outside line ${line}'s range 1..${maximumColumn}.`);
    }
    return sourceFile.getPositionOfLineAndCharacter(line - 1, column - 1);
  }

  symbolRecords(fileFilter) {
    const records = [];
    for (const sourceFile of this.sourceFiles) {
      if (fileFilter && !pathsEqual(sourceFile.fileName, fileFilter.fileName)) continue;
      walk(sourceFile, node => {
        const kindName = this.modules.ast.SyntaxKind[node.kind];
        const kind = declarationKinds.get(kindName);
        if (!kind || isAliasDeclaration(kindName)) return;
        const nameNode = declarationNameNode(node, kindName);
        if (!nameNode) return;
        const symbol = this.project.checker.getSymbolAtLocation(nameNode);
        if (!symbol || !symbol.declarations?.length) return;
        const canonical = this.canonicalSymbol(symbol);
        const range = this.range(sourceFile, node);
        const nameRange = this.range(sourceFile, nameNode);
        const selector = createSelector(this.displayPath(sourceFile.fileName), node.getStart(sourceFile), node.getEnd());
        const displayName = this.displayName(symbol, node);
        records.push({
          sourceFile,
          node,
          nameNode,
          symbol,
          canonical,
          kind,
          name: symbol.name,
          displayName,
          selector,
          range,
          nameRange,
          documentation: normalizeOptional(symbol.getDocumentationComment(this.project.checker)),
          publicDeclaration: isPublicDeclaration(node, kindName, this.modules.ast.SyntaxKind),
        });
      });
    }
    return records.sort(compareRecords);
  }

  canonicalSymbol(symbol) {
    if ((symbol.flags & this.modules.sync.SymbolFlags.Alias) !== 0) {
      const aliased = this.project.checker.getAliasedSymbol(symbol);
      if (!this.project.checker.isUnknownSymbol(aliased)) return aliased;
    }
    return symbol;
  }

  displayName(symbol, node) {
    const names = [symbol.name];
    let parent = symbol.getParent();
    while (parent) {
      const name = parent.name;
      if (name && !name.startsWith('"') && !name.includes('/') && !name.includes('\\') && !name.startsWith('__')) {
        names.unshift(name);
      }
      parent = parent.getParent();
    }
    if (names.length === 1) {
      let current = node.parent;
      while (current && current.kind !== this.modules.ast.SyntaxKind.SourceFile) {
        const candidate = declarationNameNode(current, this.modules.ast.SyntaxKind[current.kind]);
        const text = candidate?.text;
        if (typeof text === "string" && text !== names[0]) names.unshift(text);
        current = current.parent;
      }
    }
    return names.join(".");
  }

  range(sourceFile, node, includeJsDoc = false) {
    const start = node.getStart(sourceFile, includeJsDoc);
    const end = node.getEnd();
    const first = sourceFile.getLineAndCharacterOfPosition(start);
    const last = sourceFile.getLineAndCharacterOfPosition(end);
    return {
      path: this.displayPath(sourceFile.fileName),
      absolutePath: resolve(sourceFile.fileName),
      line: first.line + 1,
      column: first.character + 1,
      endLine: last.line + 1,
      endColumn: last.character + 1,
      start,
      end,
    };
  }

  resolveSelector(selector) {
    const record = this.symbolRecords().find(candidate => candidate.publicDeclaration && candidate.selector === selector);
    if (!record) throw new UsageError(`TypeScript symbol selector '${selector}' was not found in the current snapshot.`);
    return record;
  }

  resolveSymbol(options) {
    if (options.symbol) return this.resolveSelector(options.symbol);
    const sourceFile = this.resolveFile(options.file);
    const position = this.position(sourceFile, options.line, options.column);
    const node = this.modules.ast.getTouchingPropertyName(sourceFile, position);
    let symbol = this.project.checker.getSymbolAtLocation(node);
    if (!symbol) symbol = this.project.checker.getSymbolAtPosition(sourceFile.fileName, position);
    if (!symbol) throw new UsageError(`No TypeScript symbol was found at '${this.displayPath(sourceFile.fileName)}:${options.line}:${options.column}'.`);
    const canonical = this.canonicalSymbol(symbol);
    const records = this.symbolRecords().filter(record => record.canonical.id === canonical.id);
    if (records.length === 0) throw new UsageError(`The symbol at the requested position has no source declaration in this project.`);
    return { ...records[0], selectedSourceFile: sourceFile, selectedPosition: position, allDeclarations: records };
  }

  renderWorkspace() {
    const files = this.sourceFiles;
    const lines = ["command: workspace", `documents: ${files.length}`, "",
      `- project: ${code(basename(this.configPath))} documents: ${files.length}`];
    for (const file of files) {
      lines.push(`- project: ${code(basename(this.configPath))} kind: source path: ${code(this.displayPath(file.fileName))}`);
    }
    return lines.join("\n");
  }

  renderDiagnostics(options) {
    const maximum = options["max-results"] ? positiveInteger(options["max-results"], "max-results") : 200;
    const diagnostics = [
      ...this.project.program.getConfigFileParsingDiagnostics(),
      ...this.project.program.getProgramDiagnostics(),
      ...this.project.program.getGlobalDiagnostics(),
      ...this.project.program.getSyntacticDiagnostics(),
      ...this.project.program.getBindDiagnostics(),
      ...this.project.program.getSemanticDiagnostics(),
    ].sort((left, right) => compare(left.fileName ?? "", right.fileName ?? "") || left.pos - right.pos || left.code - right.code);
    const selected = diagnostics.slice(0, maximum);
    const lines = ["command: diagnostics", `returned: ${selected.length}/${diagnostics.length}`, `truncated: ${selected.length < diagnostics.length}`, ""];
    for (const diagnostic of selected) {
      let location = "";
      if (diagnostic.fileName) {
        const sourceFile = this.project.program.getSourceFile(diagnostic.fileName);
        if (sourceFile) {
          const start = sourceFile.getLineAndCharacterOfPosition(Math.max(0, diagnostic.pos));
          const end = sourceFile.getLineAndCharacterOfPosition(Math.max(diagnostic.pos, diagnostic.end));
          location = ` loc: ${code(`${this.displayPath(diagnostic.fileName)}:${start.line + 1}:${start.character + 1}-${end.line + 1}:${end.character + 1}`)}`;
        }
      }
      const severity = diagnosticCategoryName(this.modules.sync.DiagnosticCategory, diagnostic.category);
      lines.push(`- severity: ${severity} id: ${code(`TS${diagnostic.code}`)}${location} message: ${code(singleLine(diagnostic.text))}`);
    }
    return lines.join("\n");
  }

  renderSymbols(options) {
    const query = options.query ?? "";
    const exact = options.exact === "true";
    const caseSensitive = options["case-sensitive"] === "true";
    const maximum = options["max-results"] ? positiveInteger(options["max-results"], "max-results") : 50;
    let records = this.symbolRecords().filter(record => record.publicDeclaration);
    if (query) {
      const expected = caseSensitive ? query : query.toLocaleLowerCase("en-US");
      records = records.filter(record => {
        const name = caseSensitive ? record.name : record.name.toLocaleLowerCase("en-US");
        const displayName = caseSensitive ? record.displayName : record.displayName.toLocaleLowerCase("en-US");
        return exact ? name === expected || displayName === expected : name.includes(expected) || displayName.includes(expected);
      });
    }
    if (options.kind) {
      const kinds = symbolKinds(options.kind);
      records = records.filter(record => kinds.has(record.kind));
    }
    const selected = records.slice(0, maximum);
    return ["command: symbols", `query: ${code(query)}`, `returned: ${selected.length}/${records.length}`,
      `truncated: ${selected.length < records.length}`, "", ...selected.map(record => symbolBullet(record, true))].join("\n");
  }

  renderDocumentText(options) {
    const sourceFile = this.resolveFile(options.file);
    const range = this.range(sourceFile, sourceFile);
    return ["command: document-text", `path: ${code(this.displayPath(sourceFile.fileName))}`,
      `loc: ${code(formatRange(range))}`, fence(sourceFile.text, fenceInfo(sourceFile.fileName))].join("\n");
  }

  renderDocumentLines(options) {
    const sourceFile = this.resolveFile(options.file);
    const startLine = positiveInteger(options["start-line"], "start-line");
    const requestedEnd = positiveInteger(options["end-line"], "end-line");
    const starts = sourceFile.getLineStarts();
    if (startLine > starts.length) throw new UsageError(`Line ${startLine} is outside the document range 1..${starts.length}.`);
    const endLine = Math.min(requestedEnd, starts.length);
    if (endLine < startLine) throw new UsageError("Option '--end-line' must be greater than or equal to '--start-line'.");
    const start = starts[startLine - 1];
    const end = endLine < starts.length ? starts[endLine] : sourceFile.text.length;
    const text = sourceFile.text.slice(start, end).replace(/\r?\n$/, "");
    const endPosition = sourceFile.getLineAndCharacterOfPosition(end);
    const range = `${this.displayPath(sourceFile.fileName)}:${startLine}:1-${endPosition.line + 1}:${endPosition.character + 1}`;
    return ["command: document-lines", `path: ${code(this.displayPath(sourceFile.fileName))}`, `range: ${code(range)}`, "", fence(text, fenceInfo(sourceFile.fileName))].join("\n");
  }

  renderDocumentSymbols(options) {
    const sourceFile = this.resolveFile(options.file);
    const records = this.symbolRecords(sourceFile).filter(record => record.publicDeclaration);
    return ["command: document-symbols", `file: ${code(this.displayPath(sourceFile.fileName))}`, "", ...records.map(record => symbolBullet(record, true))].join("\n");
  }

  renderDefinition(options) {
    const resolved = this.resolveSymbol(options);
    const records = options.symbol ? [resolved] : (resolved.allDeclarations ?? this.symbolRecords().filter(record => record.canonical.id === resolved.canonical.id));
    return ["command: definition", `selector: ${code(selectorText(options, resolved))}`, "", ...records.map(record => symbolBullet(record, true))].join("\n");
  }

  renderTypeDefinition(options) {
    const resolved = this.resolveSymbol(options);
    const type = this.project.checker.getTypeAtLocation(resolved.nameNode) ?? this.project.checker.getTypeOfSymbol(resolved.canonical);
    const typeSymbol = type?.getSymbol?.() ?? type?.symbol;
    const canonical = typeSymbol ? this.canonicalSymbol(typeSymbol) : resolved.canonical;
    const records = this.symbolRecords().filter(record => record.publicDeclaration && record.canonical.id === canonical.id);
    return ["command: type-definition", `selector: ${code(selectorText(options, resolved))}`, "", ...records.map(record => symbolBullet(record, true))].join("\n");
  }

  renderReferences(options) {
    const resolved = this.resolveSymbol(options);
    const canonicalId = resolved.canonical.id;
    const declarationKeys = new Set(this.symbolRecords().filter(record => record.canonical.id === canonicalId)
      .map(record => `${record.sourceFile.fileName}:${record.nameNode.getStart(record.sourceFile)}:${record.nameNode.getEnd()}`));
    const references = [];
    for (const sourceFile of this.sourceFiles) {
      walk(sourceFile, node => {
        if (node.kind !== this.modules.ast.SyntaxKind.Identifier && node.kind !== this.modules.ast.SyntaxKind.PrivateIdentifier) return;
        const key = `${sourceFile.fileName}:${node.getStart(sourceFile)}:${node.getEnd()}`;
        if (declarationKeys.has(key)) return;
        const symbol = this.project.checker.getSymbolAtLocation(node);
        if (!symbol || this.canonicalSymbol(symbol).id !== canonicalId) return;
        references.push(this.range(sourceFile, node));
      });
    }
    references.sort(compareRanges);
    const maximum = options["max-results"] ? positiveInteger(options["max-results"], "max-results") : 50;
    const selected = references.slice(0, maximum);
    const lines = ["command: references", `selector: ${code(selectorText(options, resolved))}`,
      `symbol: ${code(resolved.selector)}`];
    if (resolved.documentation) lines.push(`documentation: ${singleLine(resolved.documentation)}`);
    lines.push(`returned: ${selected.length}/${references.length}`, `truncated: ${selected.length < references.length}`, "");
    for (const reference of selected) lines.push(`- loc: ${code(formatRange(reference))}`);
    return lines.join("\n");
  }

  renderImplementations(options) {
    const resolved = this.resolveSymbol(options);
    const records = this.symbolRecords().filter(record => record.publicDeclaration);
    const targetType = this.project.checker.getDeclaredTypeOfSymbol(resolved.canonical);
    const targetId = resolved.canonical.id;
    const implementations = records.filter(record => {
      if (record.canonical.id === targetId) return false;
      if (record.kind === "class" || record.kind === "interface") {
        const type = this.project.checker.getDeclaredTypeOfSymbol(record.canonical);
        return this.recordDerivesFrom(record, targetId, records, new Set())
          || this.typeDerivesFrom(type, targetId, new Set())
          || this.project.checker.isTypeAssignableTo(type, targetType);
      }
      if (record.name === resolved.name) {
        const containing = containingClass(record.node, this.modules.ast.SyntaxKind);
        const targetContaining = containingClass(resolved.node, this.modules.ast.SyntaxKind);
        if (!containing || !targetContaining) return false;
        const candidateSymbol = this.project.checker.getSymbolAtLocation(declarationNameNode(containing, this.modules.ast.SyntaxKind[containing.kind]));
        const targetContainerSymbol = this.project.checker.getSymbolAtLocation(declarationNameNode(targetContaining, this.modules.ast.SyntaxKind[targetContaining.kind]));
        if (!candidateSymbol || !targetContainerSymbol) return false;
        const type = this.project.checker.getDeclaredTypeOfSymbol(this.canonicalSymbol(candidateSymbol));
        const targetContainerType = this.project.checker.getDeclaredTypeOfSymbol(this.canonicalSymbol(targetContainerSymbol));
        const candidateRecord = records.find(candidate => candidate.node === containing);
        const targetContainerId = this.canonicalSymbol(targetContainerSymbol).id;
        return (candidateRecord && this.recordDerivesFrom(candidateRecord, targetContainerId, records, new Set()))
          || this.typeDerivesFrom(type, targetContainerId, new Set())
          || this.project.checker.isTypeAssignableTo(type, targetContainerType);
      }
      return false;
    });
    const unique = uniqueBy(implementations, record => record.selector);
    const maximum = options["max-results"] ? positiveInteger(options["max-results"], "max-results") : 50;
    const selected = unique.slice(0, maximum);
    return ["command: implementations", `selector: ${code(selectorText(options, resolved))}`, `symbol: ${code(resolved.selector)}`,
      `returned: ${selected.length}/${unique.length}`, `truncated: ${selected.length < unique.length}`, "", ...selected.map(record => symbolBullet(record, true))].join("\n");
  }

  typeDerivesFrom(type, targetSymbolId, visited) {
    if (!type || visited.has(type.id)) return false;
    visited.add(type.id);
    const symbol = type.getSymbol?.();
    if (symbol && this.canonicalSymbol(symbol).id === targetSymbolId) return true;
    const bases = this.project.checker.getBaseTypes(type) ?? type.getBaseTypes?.() ?? [];
    return bases.some(base => this.typeDerivesFrom(base, targetSymbolId, visited));
  }

  recordDerivesFrom(record, targetSymbolId, records, visited) {
    if (!record || visited.has(record.canonical.id)) return false;
    visited.add(record.canonical.id);
    for (const clause of record.node.heritageClauses ?? []) {
      for (const heritageType of clause.types ?? []) {
        const symbol = this.project.checker.getSymbolAtLocation(heritageType.expression);
        if (!symbol) continue;
        const baseId = this.canonicalSymbol(symbol).id;
        if (baseId === targetSymbolId) return true;
        const baseRecord = records.find(candidate => candidate.canonical.id === baseId
          && (candidate.kind === "class" || candidate.kind === "interface"));
        if (this.recordDerivesFrom(baseRecord, targetSymbolId, records, visited)) return true;
      }
    }
    return false;
  }

  renderQuickInfo(options) {
    const resolved = this.resolveSymbol(options);
    const sourceFile = resolved.selectedSourceFile ?? resolved.sourceFile;
    const position = resolved.selectedPosition ?? resolved.nameNode.getStart(sourceFile);
    const node = this.modules.ast.getTouchingPropertyName(sourceFile, position);
    const type = this.project.checker.getTypeAtLocation(node) ?? this.project.checker.getTypeOfSymbol(resolved.canonical);
    const typeText = type ? this.project.checker.typeToString(type, node) : "unknown";
    const lines = ["command: quick-info", `selector: ${code(selectorText(options, resolved))}`,
      `range: ${code(formatRange(this.range(sourceFile, node)))}`, `tags: ${code(titleCase(resolved.kind))}`, "",
      "description:", fence(`${resolved.kind} ${resolved.displayName}: ${typeText}`, "typescript")];
    if (resolved.documentation) lines.push("", "documentation:", fence(resolved.documentation, "text"));
    return lines.join("\n");
  }

  renderSignatureHelp(options) {
    const sourceFile = this.resolveFile(options.file);
    const position = this.position(sourceFile, options.line, options.column);
    let node = this.modules.ast.getTouchingToken(sourceFile, Math.max(0, position - 1));
    while (node && node.kind !== this.modules.ast.SyntaxKind.CallExpression && node.kind !== this.modules.ast.SyntaxKind.NewExpression) node = node.parent;
    if (!node) throw new UsageError("No call expression was found at the requested TypeScript position.");
    const signature = this.project.checker.getResolvedSignature(node);
    if (!signature) throw new UsageError("The native TypeScript API did not resolve a signature at the requested position.");
    const parameters = signature.getParameters();
    const parameterTexts = parameters.map((parameter, index) => {
      const parameterType = this.project.checker.getParameterType(signature, index);
      return `${parameter.name}: ${parameterType ? this.project.checker.typeToString(parameterType) : "unknown"}`;
    });
    const returnType = this.project.checker.getReturnTypeOfSignature(signature);
    const declaration = signature.declaration?.resolve(this.project);
    const signatureName = declaration ? declarationNameNode(declaration, this.modules.ast.SyntaxKind[declaration.kind])?.text : "call";
    const label = `${signatureName ?? "call"}(${parameterTexts.join(", ")}): ${returnType ? this.project.checker.typeToString(returnType) : "unknown"}`;
    const argumentsList = node.arguments ?? [];
    let activeParameter = 0;
    for (let index = 0; index < argumentsList.length; index++) {
      if (argumentsList[index].getEnd() < position) activeParameter = index + 1;
    }
    if (parameters.length > 0) activeParameter = Math.min(activeParameter, parameters.length - 1);
    const range = this.range(sourceFile, node);
    return ["command: signature-help", `selector: ${code(`${this.displayPath(sourceFile.fileName)}:${options.line}:${options.column}-${options.line}:${options.column}`)}`,
      `active-signature: 0`, `active-parameter: ${activeParameter}`, "", `- signature: ${code(label)}`].join("\n");
  }

  renderSymbolSource(options) {
    if (!options.symbol) throw new UsageError("Missing required option '--symbol'.");
    const record = this.resolveSelector(options.symbol);
    const range = this.range(record.sourceFile, record.node, true);
    const text = record.sourceFile.text.slice(range.start, range.end);
    return ["command: symbol-source", `symbol: ${code(record.selector)}`, "", symbolBullet(record, false), "",
      `loc: ${code(formatRange(range))}`, fence(text, fenceInfo(record.sourceFile.fileName))].join("\n");
  }

  buildCorpus() {
    const records = this.symbolRecords().filter(record => record.publicDeclaration).map(record => {
      const declarationText = record.node.getText(record.sourceFile);
      const signature = firstLine(declarationText);
      const comments = record.documentation;
      return {
        symbolKey: record.selector,
        projectPath: this.configPath,
        projectName: basename(this.configPath),
        kind: record.kind,
        name: record.name,
        displayName: record.displayName,
        selector: record.selector,
        path: resolve(record.sourceFile.fileName),
        line: record.nameRange.line,
        column: record.nameRange.column,
        endLine: record.nameRange.endLine,
        endColumn: record.nameRange.endColumn,
        documentation: record.documentation,
        signature,
        comments,
        body: declarationText,
        nameTokens: tokenizeName(record.name).join(" "),
        containingTokens: tokenizeName(record.displayName).join(" "),
        detailsTokens: tokenizeName(`${record.kind} ${record.documentation ?? ""} ${signature}`).join(" "),
        pathTokens: tokenizeName(this.displayPath(record.sourceFile.fileName)).join(" "),
        bodyTokens: tokenizeName(declarationText).join(" "),
      };
    });
    const hash = createHash("sha256");
    hash.update(this.configPath);
    for (const sourceFile of this.sourceFiles) {
      hash.update("\0");
      hash.update(this.displayPath(sourceFile.fileName));
      hash.update("\0");
      hash.update(sourceFile.text);
    }
    return { targetPath: this.configPath, fingerprint: hash.digest("hex"), records, state: this.debugState() };
  }
}

async function loadNativePreview(packageRoot) {
  const packageJson = resolve(packageRoot, "package.json");
  const resolver = createRequire(packageJson);
  let syncPath;
  let astPath;
  try {
    syncPath = resolver.resolve("@typescript/native-preview/unstable/sync");
    astPath = resolver.resolve("@typescript/native-preview/unstable/ast");
  }
  catch (error) {
    throw new Error(`Unable to load @typescript/native-preview/unstable/sync from '${packageRoot}': ${error instanceof Error ? error.message : error}`);
  }
  return {
    sync: await import(pathToFileURL(syncPath).href),
    ast: await import(pathToFileURL(astPath).href),
  };
}

function walk(node, visitor) {
  visitor(node);
  node.forEachChild(child => walk(child, visitor));
}

function declarationNameNode(node, kindName) {
  if (node.name && typeof node.name === "object") return node.name;
  if (kindName === "Constructor") return node;
  if ((kindName === "FunctionExpression" || kindName === "ArrowFunction") && node.parent?.name) return node.parent.name;
  return undefined;
}

function isAliasDeclaration(kindName) {
  return kindName === "ImportSpecifier" || kindName === "ImportClause" || kindName === "NamespaceImport" || kindName === "ExportSpecifier";
}

function containingClass(node, syntaxKind) {
  let current = node.parent;
  while (current) {
    if (current.kind === syntaxKind.ClassDeclaration || current.kind === syntaxKind.ClassExpression || current.kind === syntaxKind.InterfaceDeclaration) return current;
    current = current.parent;
  }
  return undefined;
}

function isPublicDeclaration(node, kindName, syntaxKind) {
  if (kindName === "Parameter") return false;
  if (kindName === "VariableDeclaration"
      && (node.initializer?.kind === syntaxKind.ArrowFunction || node.initializer?.kind === syntaxKind.FunctionExpression)) {
    return false;
  }

  let current = node.parent;
  while (current && current.kind !== syntaxKind.SourceFile) {
    if (current.kind === syntaxKind.FunctionDeclaration
        || current.kind === syntaxKind.FunctionExpression
        || current.kind === syntaxKind.ArrowFunction
        || current.kind === syntaxKind.MethodDeclaration
        || current.kind === syntaxKind.Constructor
        || current.kind === syntaxKind.GetAccessor
        || current.kind === syntaxKind.SetAccessor) {
      return false;
    }
    current = current.parent;
  }
  return true;
}

function sourceExtension(fileName) {
  const lower = fileName.toLocaleLowerCase("en-US");
  for (const extension of [".tsx", ".mts", ".cts", ".jsx", ".mjs", ".cjs", ".ts", ".js"]) {
    if (lower.endsWith(extension)) return extension;
  }
  return extname(lower);
}

function isWithin(root, candidate) {
  const value = relative(resolve(root), resolve(candidate));
  return value !== ".." && !value.startsWith(`..${sep}`) && !isAbsolute(value);
}

function pathsEqual(left, right) {
  return process.platform === "win32"
    ? resolve(left).toLocaleLowerCase("en-US") === resolve(right).toLocaleLowerCase("en-US")
    : resolve(left) === resolve(right);
}

function compare(left, right) { return left < right ? -1 : left > right ? 1 : 0; }
function compareRecords(left, right) { return compare(left.displayName, right.displayName) || compare(left.range.path, right.range.path) || left.range.start - right.range.start; }
function compareRanges(left, right) { return compare(left.path, right.path) || left.start - right.start || left.end - right.end; }
function normalizeSlashes(value) { return value.split(sep).join("/"); }
function normalizeOptional(value) { const normalized = singleLine(value ?? "").trim(); return normalized || null; }
function singleLine(value) { return String(value).replace(/\s+/g, " ").trim(); }
function firstLine(value) { return value.split(/\r?\n/, 1)[0].trim(); }
function titleCase(value) { return value.length === 0 ? value : value[0].toUpperCase() + value.slice(1); }
function symbolKinds(value) {
  if (value === "type") return new Set(["class", "interface", "type", "enum"]);
  if (value === "member") return new Set(["method", "property", "field", "event"]);
  if (value === "function" || value === "constructor") return new Set(["method"]);
  if (value === "variable") return new Set(["field"]);
  if (value === "type-alias") return new Set(["type"]);
  return new Set([value]);
}

function positiveInteger(value, name) {
  const number = Number(value);
  if (!Number.isSafeInteger(number) || number < 1) throw new UsageError(`Option '--${name}' must be an integer greater than or equal to 1.`);
  return number;
}

function createSelector(displayPath, start, end) {
  return `ts:${Buffer.from(displayPath, "utf8").toString("base64url")}:${start}:${end}`;
}

function selectorText(options, record) {
  return options.symbol ?? `${record.range.path}:${options.line}:${options.column}-${options.line}:${options.column}`;
}

function symbolBullet(record, includeDocumentation) {
  let value = `- kind: ${titleCase(record.kind)} name: ${code(record.displayName)} loc: ${code(formatRange(record.nameRange))} id: ${code(record.selector)}`;
  if (includeDocumentation && record.documentation) value += `\n  documentation: ${singleLine(record.documentation)}`;
  return value;
}

function formatRange(range) { return `${range.path}:${range.line}:${range.column}-${range.endLine}:${range.endColumn}`; }

function code(value) {
  const text = String(value);
  let run = 0;
  let longest = 0;
  for (const character of text) {
    if (character === "`") { run++; longest = Math.max(longest, run); } else run = 0;
  }
  const delimiter = "`".repeat(longest + 1);
  return `${delimiter}${text}${delimiter}`;
}

function fence(text, info) {
  const value = String(text);
  let run = 0;
  let longest = 0;
  for (const character of value) {
    if (character === "`") { run++; longest = Math.max(longest, run); } else run = 0;
  }
  const delimiter = "`".repeat(Math.max(3, longest + 1));
  return `${delimiter}${info}\n${value}\n${delimiter}`;
}

function fenceInfo(fileName) {
  const extension = sourceExtension(fileName);
  return extension === ".js" || extension === ".jsx" || extension === ".mjs" || extension === ".cjs" ? "javascript" : "typescript";
}

function diagnosticCategoryName(categories, value) {
  const name = categories[value] ?? "Message";
  return titleCase(String(name).toLocaleLowerCase("en-US"));
}

function tokenizeName(value) {
  return String(value)
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/[^\p{L}\p{N}_]+/gu, " ")
    .split(/[_\s]+/)
    .map(token => token.toLocaleLowerCase("en-US"))
    .filter(Boolean);
}

function uniqueBy(values, keySelector) {
  const seen = new Set();
  return values.filter(value => {
    const key = keySelector(value);
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

async function runBridge() {
  const configIndex = process.argv.indexOf("--config");
  const packageIndex = process.argv.indexOf("--native-preview-root");
  if (configIndex < 0 || !process.argv[configIndex + 1] || packageIndex < 0 || !process.argv[packageIndex + 1]) {
    process.stderr.write("Usage: node bridge.mjs --config <tsconfig.json|jsconfig.json> --native-preview-root <package-root>\n");
    process.exitCode = 2;
    return;
  }
  const workspace = new NativePreviewWorkspace(process.argv[configIndex + 1], process.argv[packageIndex + 1]);
  const lines = createInterface({ input: process.stdin, crlfDelay: Infinity });
  let chain = Promise.resolve();
  lines.on("line", line => {
    chain = chain.then(async () => {
      let request;
      try {
        request = JSON.parse(line);
        let result;
        if (request.command === "refresh") result = { exitCode: 0, stdout: "", stderr: "", state: await workspace.refresh() };
        else if (request.command === "close") { workspace.close(); result = { exitCode: 0, stdout: "", stderr: "", state: workspace.debugState() }; }
        else result = await workspace.execute(request.command, request.options ?? {});
        process.stdout.write(`${JSON.stringify({ id: request.id, ...result })}\n`);
      }
      catch (error) {
        process.stdout.write(`${JSON.stringify({ id: request?.id ?? null, exitCode: 1, stdout: `error: ${error?.constructor?.name ?? "Error"}\nmessage: ${singleLine(error instanceof Error ? error.message : error)}`, stderr: "" })}\n`);
      }
    });
  });
  await new Promise(resolveDone => lines.once("close", resolveDone));
  await chain;
  workspace.close();
}

const invokedPath = process.argv[1] ? pathToFileURL(resolve(process.argv[1])).href : "";
if (import.meta.url === invokedPath) await runBridge();
