import { useEffect, useRef } from "react";
import { EditorState } from "@codemirror/state";
import {
  EditorView,
  drawSelection,
  highlightActiveLine,
  highlightActiveLineGutter,
  keymap,
  lineNumbers,
} from "@codemirror/view";
import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands";
import {
  HighlightStyle,
  StreamLanguage,
  bracketMatching,
  indentOnInput,
  indentUnit,
  syntaxHighlighting,
} from "@codemirror/language";
import { linter, lintGutter, type Diagnostic } from "@codemirror/lint";
import { tags as t } from "@lezer/highlight";
import { parse, printParseErrorCode, type ParseError } from "jsonc-parser";

const PROBLEM_TEXT: Record<string, string> = {
  InvalidSymbol: "Unexpected character",
  InvalidNumberFormat: "Invalid number",
  PropertyNameExpected: "Expected a name in double quotes",
  ValueExpected: "Expected a value",
  ColonExpected: "Expected ':'",
  CommaExpected: "Expected ','",
  CloseBraceExpected: "Missing '}'",
  CloseBracketExpected: "Missing ']'",
  EndOfFileExpected: "Unexpected text after the end",
  InvalidCommentToken: "Invalid comment",
  UnexpectedEndOfComment: "Unclosed comment",
  UnexpectedEndOfString: "Unclosed string",
  UnexpectedEndOfNumber: "Incomplete number",
  InvalidUnicode: "Invalid unicode escape",
  InvalidEscapeCharacter: "Invalid escape character",
  InvalidCharacter: "Invalid character",
};

export interface JsoncProblem {
  from: number;
  to: number;
  message: string;
}

/** JSON with comments and trailing commas, like Deadworks' own config loader accepts. */
export function jsoncProblems(text: string): JsoncProblem[] {
  const errors: ParseError[] = [];
  parse(text, errors, { allowTrailingComma: true, disallowComments: false });
  return errors.map((e) => ({
    from: Math.min(e.offset, text.length),
    to: Math.min(e.offset + Math.max(e.length, 1), text.length),
    message: PROBLEM_TEXT[printParseErrorCode(e.error)] ?? "Syntax error",
  }));
}

interface JsoncState {
  depth: number;
  inBlockComment: boolean;
}

/** Small JSONC tokenizer: the lezer JSON grammar treats comments as errors. */
const jsoncLanguage = StreamLanguage.define<JsoncState>({
  name: "jsonc",
  startState: () => ({ depth: 0, inBlockComment: false }),
  token(stream, state) {
    if (state.inBlockComment) {
      if (stream.skipTo("*/")) {
        stream.pos += 2;
        state.inBlockComment = false;
      } else {
        stream.skipToEnd();
      }
      return "comment";
    }
    if (stream.eatSpace()) return null;
    if (stream.match("//")) {
      stream.skipToEnd();
      return "comment";
    }
    if (stream.match("/*")) {
      state.inBlockComment = true;
      return "comment";
    }
    const ch = stream.peek();
    if (ch === '"') {
      stream.next();
      let escaped = false;
      while (!stream.eol()) {
        const c = stream.next();
        if (c === '"' && !escaped) break;
        escaped = !escaped && c === "\\";
      }
      return stream.match(/^\s*:/, false) ? "propertyName" : "string";
    }
    if (stream.match(/^-?\d+(\.\d+)?([eE][+-]?\d+)?/)) return "number";
    if (stream.match(/^(true|false)\b/)) return "bool";
    if (stream.match(/^null\b/)) return "null";
    const c = stream.next();
    if (c === "{" || c === "[") state.depth++;
    if (c === "}" || c === "]") state.depth = Math.max(0, state.depth - 1);
    return "punctuation";
  },
  indent(state, textAfter, cx) {
    const closing = /^\s*[}\]]/.test(textAfter) ? 1 : 0;
    return Math.max(0, state.depth - closing) * cx.unit;
  },
  languageData: {
    commentTokens: { line: "//", block: { open: "/*", close: "*/" } },
    indentOnInput: /^\s*[}\]]$/,
  },
});

const highlight = HighlightStyle.define([
  { tag: t.propertyName, color: "#8cc8ff" },
  { tag: t.string, color: "#a5e8b8" },
  { tag: t.number, color: "#f0c070" },
  { tag: [t.bool, t.null], color: "#d49cff" },
  { tag: t.comment, color: "#6a6c75", fontStyle: "italic" },
  { tag: t.punctuation, color: "#a0a0a6" },
]);

const theme = EditorView.theme(
  {
    "&": {
      height: "100%",
      fontSize: "13px",
      color: "var(--text-secondary)",
      backgroundColor: "var(--bg)",
      border: "1px solid var(--border)",
      borderRadius: "2px",
    },
    "&.cm-focused": { outline: "none", borderColor: "var(--accent)" },
    ".cm-scroller": { fontFamily: "Consolas, 'Courier New', monospace", lineHeight: "1.55" },
    ".cm-content": { caretColor: "var(--accent)", userSelect: "text" },
    ".cm-cursor, .cm-dropCursor": { borderLeftColor: "var(--accent)" },
    "&.cm-focused .cm-selectionBackground, .cm-selectionBackground, .cm-content ::selection": {
      backgroundColor: "rgba(105, 232, 153, 0.22)",
    },
    ".cm-gutters": {
      backgroundColor: "var(--bg-panel)",
      color: "#4a4c55",
      borderRight: "1px solid var(--border)",
    },
    ".cm-activeLine": { backgroundColor: "rgba(255, 255, 255, 0.03)" },
    ".cm-activeLineGutter": { backgroundColor: "rgba(255, 255, 255, 0.05)", color: "var(--text-muted)" },
    ".cm-matchingBracket": { backgroundColor: "rgba(105, 232, 153, 0.18)", outline: "none" },
    ".cm-tooltip": {
      backgroundColor: "var(--bg-card)",
      border: "1px solid var(--border)",
      color: "var(--text)",
    },
    ".cm-diagnostic-error": { borderLeftColor: "var(--ping-bad)" },
    ".cm-lintRange-error": {
      backgroundImage: "none",
      textDecoration: "underline wavy var(--ping-bad)",
      textUnderlineOffset: "3px",
    },
  },
  { dark: true }
);

const jsoncLinter = linter(
  (view) =>
    jsoncProblems(view.state.doc.toString()).map(
      (p): Diagnostic => ({ from: p.from, to: p.to, severity: "error", message: p.message })
    ),
  { delay: 250 }
);

interface JsoncEditorProps {
  /** Only read on mount; remount with a new `key` to load different text. */
  initialValue: string;
  onChange: (text: string) => void;
  /** Only read on mount. The text can still be selected and copied. */
  readOnly?: boolean;
}

export default function JsoncEditor({ initialValue, onChange, readOnly }: JsoncEditorProps) {
  const host = useRef<HTMLDivElement>(null);
  const onChangeRef = useRef(onChange);
  onChangeRef.current = onChange;

  useEffect(() => {
    if (!host.current) return;
    const view = new EditorView({
      parent: host.current,
      state: EditorState.create({
        doc: initialValue,
        extensions: [
          lineNumbers(),
          highlightActiveLineGutter(),
          highlightActiveLine(),
          drawSelection(),
          history(),
          indentOnInput(),
          bracketMatching(),
          indentUnit.of("  "),
          EditorState.tabSize.of(2),
          EditorState.readOnly.of(!!readOnly),
          keymap.of([...defaultKeymap, ...historyKeymap, indentWithTab]),
          jsoncLanguage,
          syntaxHighlighting(highlight),
          jsoncLinter,
          lintGutter(),
          theme,
          EditorView.updateListener.of((u) => {
            if (u.docChanged) onChangeRef.current(u.state.doc.toString());
          }),
        ],
      }),
    });
    view.focus();
    return () => view.destroy();
    // The editor owns its document after mount; initialValue is read once.
  }, []);

  return <div ref={host} style={{ height: "100%", minHeight: 0 }} />;
}
