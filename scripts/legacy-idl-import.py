#!/usr/bin/env python3
"""legacy-idl-import - import IDL written for keylist-pragma DDS implementations into CycloneDDS.NET C# (DD-013, T-028).

Pipeline (see docs/design/DD-013-Legacy-Idl-Import.md):
  1. stage    copy the inputs and the include folders into <work>/src/<folder>, CRLF -> LF, '\\' -> '/' in #include (I-8)
  2. pragmas  parse '#pragma keylist' and '#pragma topic' (both QoS spellings) into <work>/topics.json, remove the topic lines
  3. import   run CycloneDDS.IdlImporter (idlc -l json, -f keylist) per master IDL into <work>/raw
  4. post     keyless keylist types -> [DdsTopic] (I-3), [DdsQos] from the table (I-2), topic names (I-6),
              optional /// docs from trailing member comments (I-7), provenance header, --only closure
  5. verify   (--verify) build the output against the CycloneDDS.NET package, take the IDL its code generator emits and
              compare it type by type with the cleaned input IDL; compare [DdsQos] / [DdsTopic] with the topic table

Everything is deterministic: no dates, sorted traversal, LF line endings, so a rerun is byte-identical.
Python 3.8+, standard library only. Works on Linux and Windows (R-012).
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys

VERSION = "1"
MARKER = "legacy-idl-import"
TOPIC_NAME_POLICIES = ("scoped", "underscore", "bare", "typename")


class ImportError_(Exception):
    """A user-facing error: message is printed, exit code 2."""


# ----------------------------------------------------------------------------------------------
# IDL tokenizer and structural parser (shared by the pragma step, the doc-comment step and verify)
# ----------------------------------------------------------------------------------------------

TOKEN_RE = re.compile(
    r"""(?P<ws>[ \t\r\f\v]+)
      |(?P<nl>\n)
      |(?P<lc>//[^\n]*)
      |(?P<bc>/\*.*?\*/)
      |(?P<str>"(?:\\.|[^"\\\n])*")
      |(?P<chr>'(?:\\.|[^'\\\n])+')
      |(?P<num>0[xX][0-9a-fA-F]+|\d+(?:\.\d+)?)
      |(?P<id>(?:::)?[A-Za-z_]\w*(?:::[A-Za-z_]\w*)*)
      |(?P<p>[{}()<>;,:=\[\]@\-+*/|&~^%])
    """,
    re.X | re.S,
)


class Tok(object):
    __slots__ = ("kind", "text", "line")

    def __init__(self, kind, text, line):
        self.kind, self.text, self.line = kind, text, line

    def __repr__(self):
        return "%s:%r@%d" % (self.kind, self.text, self.line)


def tokenize(text, path="<idl>"):
    """Returns (tokens, comments) - comments maps a line number to the text of a '//' or single-line '/* */' comment on it."""
    toks, comments = [], {}
    pos, line, n = 0, 1, len(text)
    at_line_start = True
    while pos < n:
        if at_line_start:
            m = re.compile(r"[ \t]*#[^\n]*").match(text, pos)
            if m:  # preprocessor line (continuations are not used by our inputs)
                raw = m.group(0).strip()
                cm = re.search(r"//.*$", raw)
                if cm:
                    comments[line] = cm.group(0)[2:].strip()
                    raw = raw[: cm.start()].rstrip()
                toks.append(Tok("pp", raw, line))
                pos = m.end()
                at_line_start = False
                continue
        m = TOKEN_RE.match(text, pos)
        if not m:
            raise ImportError_("%s:%d: cannot tokenize near %r" % (path, line, text[pos:pos + 20]))
        k = m.lastgroup
        s = m.group(0)
        pos = m.end()
        if k == "nl":
            line += 1
            at_line_start = True
            continue
        at_line_start = at_line_start and k == "ws"
        if k == "ws":
            continue
        if k == "lc":
            comments[line] = s[2:].strip()
        elif k == "bc":
            if "\n" not in s:
                comments[line] = s[2:-2].strip()
            line += s.count("\n")
        else:
            toks.append(Tok(k, s, line))
    return toks, comments


PRIMS = {
    "boolean": "boolean", "octet": "octet", "char": "char", "float": "float", "double": "double",
    "short": "int16", "int16": "int16", "int8": "int8", "uint8": "uint8", "int32": "int32", "uint32": "uint32",
    "int64": "int64", "uint64": "uint64", "uint16": "uint16",
}


class Parser(object):
    """Parses one IDL text into declarations; names are resolved later by Model."""

    def __init__(self, toks, comments, path):
        self.t, self.comments, self.path, self.i = toks, comments, path, 0
        self.decls = []    # dicts, in order
        self.pragmas = []  # {scope, text, line}

    def err(self, msg, tok=None):
        tok = tok or (self.t[self.i] if self.i < len(self.t) else None)
        raise ImportError_("%s:%d: %s" % (self.path, tok.line if tok else 0, msg))

    def peek(self, k=0):
        j = self.i + k
        return self.t[j] if j < len(self.t) else Tok("eof", "", 0)

    def eat(self, text=None):
        tok = self.peek()
        if tok.kind == "eof" or (text is not None and tok.text != text):
            self.err("expected %r, found %r" % (text, tok.text))
        self.i += 1
        return tok

    def accept(self, text):
        if self.peek().text == text and self.peek().kind != "str":
            self.i += 1
            return True
        return False

    def parse(self):
        self.defs((), top=True)
        return self

    def annotations(self):
        ann = {}
        while self.peek().text == "@" and self.peek().kind == "p":
            self.eat("@")
            name = self.eat().text
            args = ""
            if self.peek().text == "(":
                depth, parts = 0, []
                while True:
                    tk = self.eat()
                    if tk.text == "(":
                        depth += 1
                        if depth == 1:
                            continue
                    if tk.text == ")":
                        depth -= 1
                        if depth == 0:
                            break
                    parts.append(tk.text)
                args = "".join(parts)
            ann[name.lstrip(":")] = args
        return ann

    def defs(self, scope, top=False):
        while True:
            tok = self.peek()
            if tok.kind == "eof":
                if not top:
                    self.err("unexpected end of file")
                return
            if tok.kind == "pp":
                self.i += 1
                if tok.text.startswith("#pragma"):
                    self.pragmas.append({"scope": scope, "text": tok.text, "line": tok.line})
                continue
            if tok.text == "}" and not top:
                return
            ann = self.annotations()
            tok = self.peek()
            kw = tok.text
            if kw == "module":
                self.i += 1
                name = self.eat().text
                self.eat("{")
                self.decls.append({"kind": "module", "name": scope + (name,), "line": tok.line})
                self.defs(scope + (name,))
                self.eat("}")
                self.eat(";")
            elif kw == "typedef":
                self.i += 1
                ty = self.type_spec()
                while True:
                    nm = self.eat().text
                    dims = self.dims()
                    self.decls.append({"kind": "typedef", "name": scope + (nm,), "scope": scope, "type": ty,
                                       "dims": dims, "line": tok.line})
                    if not self.accept(","):
                        break
                self.eat(";")
            elif kw == "enum":
                self.i += 1
                name = self.eat().text
                self.eat("{")
                items, nxt = [], 0
                while True:
                    eann = self.annotations()
                    en = self.eat().text
                    if "value" in eann:
                        nxt = int(eann["value"], 0)
                    items.append((en, nxt))
                    nxt += 1
                    if not self.accept(","):
                        break
                    if self.peek().text == "}":
                        break
                self.eat("}")
                self.eat(";")
                bb = int(ann.get("bit_bound", "32"), 0)
                self.decls.append({"kind": "enum", "name": scope + (name,), "scope": scope, "bit_bound": bb,
                                   "values": items, "line": tok.line})
            elif kw == "struct":
                self.i += 1
                name = self.eat().text
                if self.accept(";"):
                    continue  # forward declaration
                if self.accept(":"):
                    self.err("struct inheritance is not supported")
                self.eat("{")
                members = []
                while self.peek().text != "}":
                    mann = self.annotations()
                    ty = self.type_spec()
                    while True:
                        mn = self.eat().text
                        dims = self.dims()
                        members.append({"name": mn, "type": ty, "dims": dims, "ann": mann})
                        if not self.accept(","):
                            break
                    semi = self.eat(";")
                    for mm in members:
                        mm.setdefault("line", semi.line)
                        mm.setdefault("comment", self.comments.get(semi.line, ""))
                self.eat("}")
                self.eat(";")
                self.decls.append({"kind": "struct", "name": scope + (name,), "scope": scope, "ann": ann,
                                   "members": members, "line": tok.line})
            elif kw == "union":
                self.i += 1
                name = self.eat().text
                if self.accept(";"):
                    continue
                self.eat("switch")
                self.eat("(")
                disc = self.type_spec()
                self.eat(")")
                self.eat("{")
                cases = []
                while self.peek().text != "}":
                    labels = []
                    while self.peek().text in ("case", "default"):
                        if self.accept("default"):
                            labels.append("default")
                        else:
                            self.eat("case")
                            labels.append(self.const_text(":"))
                        self.eat(":")
                    if not labels:
                        self.err("expected 'case' or 'default'")
                    mann = self.annotations()
                    ty = self.type_spec()
                    mn = self.eat().text
                    dims = self.dims()
                    semi = self.eat(";")
                    cases.append({"labels": labels, "name": mn, "type": ty, "dims": dims, "ann": mann,
                                  "line": semi.line, "comment": self.comments.get(semi.line, "")})
                self.eat("}")
                self.eat(";")
                self.decls.append({"kind": "union", "name": scope + (name,), "scope": scope, "ann": ann,
                                   "disc": disc, "cases": cases, "line": tok.line})
            elif kw == "const":
                while self.eat().text != ";":
                    pass
            else:
                self.err("unsupported or unexpected construct %r" % kw)

    def const_text(self, stop):
        parts = []
        while self.peek().text != stop:
            parts.append(self.eat().text)
        return "".join(parts)

    def dims(self):
        dims = []
        while self.accept("["):
            dims.append(self.const_text("]"))
            self.eat("]")
        return dims

    def type_spec(self):
        tok = self.eat()
        w = tok.text
        if w == "sequence":
            self.eat("<")
            el = self.type_spec()
            bound = None
            if self.accept(","):
                bound = self.const_text(">")
            self.eat(">")
            return ("seq", el, bound)
        if w == "string":
            bound = None
            if self.accept("<"):
                bound = self.const_text(">")
                self.eat(">")
            return ("string", bound)
        if w == "unsigned":
            nxt = self.eat().text
            if nxt == "short":
                return ("prim", "uint16")
            if nxt == "long":
                if self.peek().text == "long":
                    self.i += 1
                    return ("prim", "uint64")
                return ("prim", "uint32")
            self.err("bad 'unsigned' type", tok)
        if w == "long":
            if self.peek().text == "long":
                self.i += 1
                return ("prim", "int64")
            return ("prim", "int32")
        if w in PRIMS:
            return ("prim", PRIMS[w])
        if tok.kind == "id":
            return ("named", w)
        self.err("unsupported type %r" % w, tok)


def parse_idl_text(text, path):
    toks, comments = tokenize(text, path)
    return Parser(toks, comments, path).parse()


# ----------------------------------------------------------------------------------------------
# Model: resolved, normalised view of a set of IDL files (used for verify and for doc comments)
# ----------------------------------------------------------------------------------------------

def sc(parts):
    return "::".join(parts)


class Model(object):
    def __init__(self):
        self.decls = {}      # scoped name -> decl
        self.enumerators = {}  # scoped enumerator -> value
        self.keylists = {}   # scoped type -> [keys]

    def add(self, parser):
        for d in parser.decls:
            if d["kind"] == "module":
                continue
            name = sc(d["name"])
            if name in self.decls and d["kind"] != "typedef":
                prev = self.decls[name]
                if prev["kind"] != d["kind"]:
                    raise ImportError_("%s declared twice with different kinds" % name)
            self.decls[name] = d
            if d["kind"] == "enum":
                for en, val in d["values"]:
                    self.enumerators[sc(d["scope"] + (en,))] = val

    def resolve(self, name, scope):
        """Scoped name for a (possibly relative) type name seen in `scope` (tuple)."""
        if name.startswith("::"):
            return name[2:]
        for i in range(len(scope), -1, -1):
            cand = sc(scope[:i] + (name,))
            if cand in self.decls or cand in self.enumerators:
                return cand
        return name  # unresolved: compared as is

    def norm_type(self, ty, scope, dims=()):
        """Normalised type string; typedefs are expanded, arrays flattened."""
        base, dl = self._norm(ty, scope)
        alld = [str(x) for x in dims] + dl
        return base + "".join("[%s]" % x for x in alld)

    def _norm(self, ty, scope):
        k = ty[0]
        if k == "prim":
            return ty[1], []
        if k == "string":
            return ("string<%s>" % ty[1]) if ty[1] else "string", []
        if k == "seq":
            el, eld = self._norm(ty[1], scope)
            el += "".join("[%s]" % x for x in eld)
            return ("sequence<%s,%s>" % (el, ty[2]) if ty[2] else "sequence<%s>" % el), []
        if k == "named":
            full = self.resolve(ty[1], scope)
            d = self.decls.get(full)
            if d is not None and d["kind"] == "typedef":
                b, dl = self._norm(d["type"], d["scope"])
                return b, [str(x) for x in d["dims"]] + dl
            return full, []
        raise ImportError_("bad type %r" % (ty,))

    def label(self, text, scope, disc_full):
        if text == "default":
            return "default"
        full = self.resolve(text, scope)
        if full in self.enumerators:
            return self.enumerators[full]
        try:
            return int(text, 0)
        except ValueError:
            pass
        if len(text) >= 3 and text[0] == "'":
            return ord(text[1])
        if text in ("TRUE", "true"):
            return 1
        if text in ("FALSE", "false"):
            return 0
        return text

    def normalised(self, keys_from_pragmas=None, name_map=None):
        """{scoped name: description} for structs, unions and enums."""
        out = {}
        keys_from_pragmas = keys_from_pragmas or {}
        for name, d in sorted(self.decls.items()):
            kind = d["kind"]
            if kind == "typedef":
                continue
            ext = "final"
            ann = d.get("ann", {})
            for e in ("final", "appendable", "mutable"):
                if e in ann:
                    ext = e
            if kind == "enum":
                out[name] = {"kind": "enum", "bit_bound": d["bit_bound"], "values": [(a, b) for a, b in d["values"]]}
            elif kind == "struct":
                members = []
                for m in d["members"]:
                    members.append({
                        "name": m["name"],
                        "type": self.norm_type(m["type"], d["scope"], m["dims"]),
                        "key": "key" in m["ann"],
                        "optional": "optional" in m["ann"],
                        "id": int(m["ann"]["id"], 0) if "id" in m["ann"] else None,
                    })
                if name in keys_from_pragmas:
                    keys = list(keys_from_pragmas[name])
                else:
                    keys = [m["name"] for m in members if m["key"]]
                out[name] = {"kind": "struct", "ext": ext, "members": members, "keys": keys}
            elif kind == "union":
                disc = self.norm_type(d["disc"], d["scope"])
                cases = []
                for c in d["cases"]:
                    labels = sorted(set(self.label(x, d["scope"], disc) for x in c["labels"]), key=lambda v: (isinstance(v, str), v))
                    cases.append({"labels": labels, "name": c["name"], "type": self.norm_type(c["type"], d["scope"], c["dims"])})
                cases.sort(key=lambda c: c["name"])
                out[name] = {"kind": "union", "ext": ext, "disc": disc, "cases": cases}
        if name_map:
            out = apply_name_map_model(out, name_map)
        return out


def map_scoped(name, name_map):
    for a, b in name_map:
        if name == a or name.startswith(a + "::"):
            return b + name[len(a):]
    return name


def apply_name_map_model(model, name_map):
    def mt(s):
        return re.sub(r"(?:::)?[A-Za-z_]\w*(?:::[A-Za-z_]\w*)+|\b[A-Za-z_]\w*\b",
                      lambda m: map_scoped(m.group(0).lstrip(":"), name_map) if "::" in m.group(0) or m.group(0) in [a for a, _ in name_map] else m.group(0), s)
    out = {}
    for k, v in model.items():
        v = json.loads(json.dumps(v))
        if v["kind"] == "struct":
            for m in v["members"]:
                m["type"] = mt(m["type"])
        elif v["kind"] == "union":
            v["disc"] = mt(v["disc"])
            for c in v["cases"]:
                c["type"] = mt(c["type"])
        out[map_scoped(k, name_map)] = v
    return out


# ----------------------------------------------------------------------------------------------
# Step 1: stage
# ----------------------------------------------------------------------------------------------

def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        h.update(f.read())
    return h.hexdigest()


INCLUDE_RE = re.compile(r'^(\s*#\s*include\s*)([<"])([^>"]*)([>"])(.*)$')


def normalise_text(raw):
    text = raw.decode("utf-8-sig") if isinstance(raw, bytes) else raw
    text = text.replace("\r\n", "\n").replace("\r", "\n")
    lines = []
    for ln in text.split("\n"):
        m = INCLUDE_RE.match(ln)
        if m:
            ln = m.group(1) + m.group(2) + m.group(3).replace("\\", "/") + m.group(4) + m.group(5)
        lines.append(ln)
    return "\n".join(lines)


def collect_idl(path):
    """[(absolute file, relative-to-path)] for a dir (recursive) or a single file."""
    if os.path.isfile(path):
        return [(os.path.abspath(path), os.path.basename(path))]
    res = []
    for root, dirs, files in os.walk(path):
        dirs.sort()
        for f in sorted(files):
            if f.lower().endswith(".idl"):
                full = os.path.join(root, f)
                res.append((os.path.abspath(full), os.path.relpath(full, path).replace(os.sep, "/")))
    return res


def stage(args, work):
    src_root = os.path.join(work, "src")
    if os.path.isdir(src_root):
        shutil.rmtree(src_root)
    os.makedirs(src_root)
    staged = {}   # relative path under src -> {"orig_sha": ..., "role": "input"|"include"}
    masters = []
    owners = {}   # top folder -> source dir

    def put(abs_file, folder, rel, role):
        dest_rel = (folder + "/" + rel) if folder else rel
        dest = os.path.join(src_root, *dest_rel.split("/"))
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        with open(abs_file, "rb") as f:
            raw = f.read()
        with open(dest, "w", encoding="utf-8", newline="\n") as f:
            f.write(normalise_text(raw))
        if dest_rel in staged:
            return
        staged[dest_rel] = {"orig_sha": hashlib.sha256(raw).hexdigest(), "role": role}
        if role == "input":
            masters.append(dest_rel)

    for item in args.input:
        item = os.path.abspath(item)
        if not os.path.exists(item):
            raise ImportError_("input not found: %s" % item)
        folder_dir = item if os.path.isdir(item) else os.path.dirname(item)
        folder = os.path.basename(folder_dir)
        if owners.setdefault(folder, folder_dir) != folder_dir:
            raise ImportError_("two input/include folders share the name %r (%s, %s)" % (folder, owners[folder], folder_dir))
        for abs_file, rel in collect_idl(item):
            put(abs_file, folder, rel, "input")
    for inc in args.include_dir or []:
        inc = os.path.abspath(inc)
        if not os.path.isdir(inc):
            raise ImportError_("include dir not found: %s" % inc)
        folder = os.path.basename(inc)
        if owners.setdefault(folder, inc) != inc:
            raise ImportError_("two input/include folders share the name %r (%s, %s)" % (folder, owners[folder], inc))
        for abs_file, rel in collect_idl(inc):
            put(abs_file, folder, rel, "include")
    if not masters:
        raise ImportError_("no input IDL files found")
    return staged, sorted(masters)


# ----------------------------------------------------------------------------------------------
# Step 2: pragmas
# ----------------------------------------------------------------------------------------------

RELIABILITY = {"reliable": "Reliable", "best_effort": "BestEffort"}
DURABILITY = {"volatile": "Volatile", "transient_local": "TransientLocal", "transient": "Transient", "persistent": "Persistent"}
HISTORY = {"keep_last": "KeepLast", "keep_all": "KeepAll"}


def parse_topic_pragma(words, qos, where):
    """Adds the policies named by the words of one '#pragma topic' line to qos (dict). Both spellings, mixable."""
    i = 0
    while i < len(words):
        w = words[i]
        if w == "reliability":
            v = words[i + 1] if i + 1 < len(words) else ""
            if v not in RELIABILITY:
                raise ImportError_("%s: unknown reliability %r" % (where, v))
            qos["reliability"] = v
            i += 2
        elif w == "durability":
            v = words[i + 1] if i + 1 < len(words) else ""
            if v not in DURABILITY:
                raise ImportError_("%s: unknown durability %r" % (where, v))
            qos["durability"] = v
            i += 2
        elif w == "history":
            i = parse_history(words, i + 1, qos, where)
        elif w in RELIABILITY:
            qos["reliability"] = w
            i += 1
        elif w in DURABILITY:
            qos["durability"] = w
            i += 1
        elif w in HISTORY:
            i = parse_history(words, i, qos, where)
        else:
            raise ImportError_("%s: unknown '#pragma topic' word %r" % (where, w))


def parse_history(words, i, qos, where):
    kind = words[i] if i < len(words) else ""
    if kind not in HISTORY:
        raise ImportError_("%s: unknown history kind %r" % (where, kind))
    i += 1
    depth = None
    if i < len(words) and re.fullmatch(r"\d+", words[i]):
        depth = int(words[i])
        i += 1
    elif kind == "keep_last":
        raise ImportError_("%s: 'keep_last' needs a depth" % where)
    qos["history"] = kind
    qos["history_depth"] = depth
    return i


def parse_pragmas(staged, work):
    """Parses every staged file; rewrites it without '#pragma topic' lines (blank lines keep the numbering).
    Returns (topics list, Model of the cleaned files)."""
    topics, model = [], Model()
    for rel in sorted(staged):
        path = os.path.join(work, "src", *rel.split("/"))
        with open(path, encoding="utf-8", newline="") as f:
            text = f.read()
        parser = parse_idl_text(text, rel)
        model.add(parser)
        entries = []   # keylist entries in order
        last_pp_keylist = None
        # pragmas appear in token order; a topic pragma belongs to the keylist before it
        # when only other pragmas lie between them. The parser only records pragmas, so walk the tokens to find adjacency.
        toks, _ = tokenize(text, rel)
        prag_by_line = {p["line"]: p for p in parser.pragmas}
        remove_lines = set()
        current = None
        pending = []   # topic pragma lines seen before the keylist they belong to (a bare '#pragma topic' may open a group)
        pp = [tk for tk in toks]
        for idx, tk in enumerate(pp):
            if tk.kind != "pp":
                if pending:
                    raise ImportError_("%s:%d: '#pragma topic' does not belong to a '#pragma keylist'" % (rel, pending[0][1]))
                current = None
                continue
            p = prag_by_line.get(tk.line)
            if p is None:
                continue
            words = p["text"].split()[1:]
            where = "%s:%d" % (rel, p["line"])
            if not words:
                raise ImportError_("%s: empty '#pragma'" % where)
            if words[0] == "keylist":
                if len(words) < 2:
                    raise ImportError_("%s: '#pragma keylist' needs a type name" % where)
                current = {"type": words[1], "keys": words[2:], "has_topic_pragma": False,
                           "qos": {}, "source": rel, "line": p["line"], "_scope": p["scope"], "_raw": words[1]}
                entries.append(current)
                for pw, pl in pending:
                    current["has_topic_pragma"] = True
                    parse_topic_pragma(pw, current["qos"], "%s:%d" % (rel, pl))
                pending = []
            elif words[0] == "topic":
                remove_lines.add(p["line"])
                nxt = pp[idx + 1] if idx + 1 < len(pp) else None
                nxt_is_keylist = (nxt is not None and nxt.kind == "pp" and nxt.text.split()[1:2] == ["keylist"])
                if current is None or (len(words) == 1 and nxt_is_keylist):
                    pending.append((words[1:], p["line"]))
                else:
                    current["has_topic_pragma"] = True
                    parse_topic_pragma(words[1:], current["qos"], where)
            else:
                raise ImportError_("%s: unknown pragma %r (only keylist and topic are understood)" % (where, words[0]))
        if pending:
            raise ImportError_("%s:%d: '#pragma topic' does not follow or precede a '#pragma keylist'" % (rel, pending[0][1]))
        for e in entries:
            topics.append(e)
        if remove_lines:
            lines = text.split("\n")
            for ln in remove_lines:
                lines[ln - 1] = ""
            with open(path, "w", encoding="utf-8", newline="\n") as f:
                f.write("\n".join(lines))
    # resolve type names now that every declaration is known
    seen = {}
    for e in topics:
        full = model.resolve(e["_raw"], e["_scope"])
        d = model.decls.get(full)
        if d is None or d["kind"] != "struct":
            raise ImportError_("%s:%d: '#pragma keylist %s' names no struct" % (e["source"], e["line"], e["_raw"]))
        e["type"] = full
        for k in e["keys"]:
            if k.split(".")[0] not in [m["name"] for m in d["members"]]:
                raise ImportError_("%s:%d: key %r is not a member of %s" % (e["source"], e["line"], k, full))
        if full in seen:
            raise ImportError_("%s:%d: second '#pragma keylist' for %s (first at %s:%d)" % (e["source"], e["line"], full, seen[full]["source"], seen[full]["line"]))
        seen[full] = e
        del e["_scope"], e["_raw"]
    topics.sort(key=lambda e: e["type"])
    # the model has been built from the files before the topic lines were blanked; blanking changes no declaration.
    return topics, model


def model_resolve_pragma_type(name, scope):
    return name


def topic_table_json(topics):
    out = []
    for e in topics:
        q = e["qos"]
        out.append({
            "type": e["type"],
            "keys": e["keys"],
            "reliability": q.get("reliability"),
            "durability": q.get("durability"),
            "history": q.get("history"),
            "history_depth": q.get("history_depth"),
            "has_topic_pragma": e["has_topic_pragma"],
            "source": e["source"],
            "line": e["line"],
        })
    return out


# ----------------------------------------------------------------------------------------------
# Step 3: importer
# ----------------------------------------------------------------------------------------------

def find_dotnet():
    d = shutil.which("dotnet")
    if d:
        return d
    for name in ("dotnet", "dotnet.exe"):
        p = os.path.join(os.path.expanduser("~"), ".dotnet", name)
        if os.path.exists(p):
            return p
    raise ImportError_("dotnet not found - run: bash scripts/setup-dotnet.sh")


def tool_env(tools):
    env = dict(os.environ)
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_NOLOGO"] = "1"
    if os.name != "nt":
        env["LD_LIBRARY_PATH"] = tools + (os.pathsep + env["LD_LIBRARY_PATH"] if env.get("LD_LIBRARY_PATH") else "")
    return env


def run_importer(args, work, staged, masters, tools):
    dll = os.path.join(tools, "CycloneDDS.IdlImporter.dll")
    idlc = os.path.join(tools, "idlc.exe" if os.name == "nt" else "idlc")
    for p in (dll, idlc):
        if not os.path.exists(p):
            raise ImportError_("--tools: %s not found" % p)
    if os.name != "nt" and not os.access(idlc, os.X_OK):
        try:
            os.chmod(idlc, os.stat(idlc).st_mode | 0o111)
        except OSError:
            raise ImportError_("idlc is not executable: %s" % idlc)
    dotnet = find_dotnet()
    raw = os.path.join(work, "raw")
    if os.path.isdir(raw):
        shutil.rmtree(raw)
    os.makedirs(raw)
    src_root = os.path.join(work, "src")
    folders = sorted(set(r.split("/")[0] for r in staged if "/" in r))
    idlc_args = "-f keylist"   # I-5
    for fo in folders:
        idlc_args += " -I " + os.path.join(src_root, fo)
    env = tool_env(tools)
    for m in masters:
        cmd = [dotnet, dll, os.path.join(src_root, *m.split("/")),
               "--source-root", src_root, "--output-root", raw,      # I-1
               "--idlc-path", idlc, "--idlc-args", idlc_args]
        p = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=env, universal_newlines=True)
        log = p.stdout
        with open(os.path.join(work, "importer-" + m.replace("/", "_") + ".log"), "w", encoding="utf-8", newline="\n") as f:
            f.write(log)
        if p.returncode != 0 or re.search(r"^\s*Error:", log, re.M):
            raise ImportError_("CycloneDDS.IdlImporter failed for %s:\n%s" % (m, log.strip()))
    return raw


# ----------------------------------------------------------------------------------------------
# Step 4: post-pass on the C#
# ----------------------------------------------------------------------------------------------

def topic_name(policy, scoped):
    if policy == "scoped":
        return scoped
    if policy == "underscore":
        return scoped.replace("::", "_")
    if policy == "bare":
        return scoped.split("::")[-1]
    if policy == "typename":
        return scoped.replace("::", ".")
    raise ImportError_("bad --topic-name %r" % policy)


def qos_attr(entry_qos):
    parts = []
    if "reliability" in entry_qos:
        parts.append("Reliability = DdsReliability." + RELIABILITY[entry_qos["reliability"]])
    if "durability" in entry_qos:
        parts.append("Durability = DdsDurability." + DURABILITY[entry_qos["durability"]])
    if "history" in entry_qos:
        parts.append("HistoryKind = DdsHistoryKind." + HISTORY[entry_qos["history"]])
        if entry_qos["history"] == "keep_last":
            parts.append("HistoryDepth = %d" % entry_qos["history_depth"])
    return "[DdsQos(%s)]" % ", ".join(parts) if parts else None


def expected_qos_props(entry_qos):
    d = {}
    if "reliability" in entry_qos:
        d["Reliability"] = "DdsReliability." + RELIABILITY[entry_qos["reliability"]]
    if "durability" in entry_qos:
        d["Durability"] = "DdsDurability." + DURABILITY[entry_qos["durability"]]
    if "history" in entry_qos:
        d["HistoryKind"] = "DdsHistoryKind." + HISTORY[entry_qos["history"]]
        if entry_qos["history"] == "keep_last":
            d["HistoryDepth"] = str(entry_qos["history_depth"])
    return d


NS_RE = re.compile(r"^namespace\s+([\w.]+)\s*$")
TYPE_RE = re.compile(r"^    (?:public\s+)?(?:unsafe\s+)?(?:partial\s+)?(struct|enum|class)\s+(\w+)")


def split_blocks(text):
    """Splits an importer C# file into (preamble lines, [namespace blocks]); each namespace block is
    {"ns": name, "types": [{"name", "kind", "lines"}]} - lines include attribute lines, excluding blank separators."""
    lines = text.split("\n")
    pre, i = [], 0
    while i < len(lines) and not NS_RE.match(lines[i]):
        pre.append(lines[i])
        i += 1
    namespaces = []
    while i < len(lines):
        m = NS_RE.match(lines[i])
        if not m:
            i += 1
            continue
        ns = {"ns": m.group(1), "types": []}
        i += 1
        if lines[i].strip() != "{":
            raise ImportError_("unexpected C# layout after namespace %s" % m.group(1))
        i += 1
        cur = []
        while i < len(lines) and lines[i] != "}":
            ln = lines[i]
            if ln.strip() == "" and not cur:
                i += 1
                continue
            cur.append(ln)
            if ln == "    }" or (ln.startswith("    ") and not ln.startswith("     ") and ln.rstrip().endswith("}") and False):
                ns["types"].append(make_type(cur))
                cur = []
            i += 1
        if cur and any(x.strip() for x in cur):
            raise ImportError_("unterminated type in namespace %s" % m.group(1))
        namespaces.append(ns)
        i += 1
    return pre, namespaces


def make_type(lines):
    for ln in lines:
        m = TYPE_RE.match(ln)
        if m:
            return {"name": m.group(2), "kind": m.group(1), "lines": lines}
    raise ImportError_("cannot recognise C# type declaration:\n" + "\n".join(lines[:4]))


def docs_for(model, scoped):
    d = model.decls.get(scoped)
    res = {}
    if d is None:
        return res
    items = d["members"] if d["kind"] == "struct" else d["cases"] if d["kind"] == "union" else []
    for m in items:
        if m.get("comment"):
            res[m["name"]] = m["comment"]
    return res


def xml_escape(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def add_member_docs(lines, docs):
    out, i = [], 0
    # find body start (after the line with '{')
    start = next(k for k, ln in enumerate(lines) if ln == "    {")
    out.extend(lines[: start + 1])
    j = start + 1
    pending = []   # attribute lines of the next member
    while j < len(lines) - 1:
        ln = lines[j]
        st = ln.strip()
        if st.startswith("["):
            pending.append(ln)
        else:
            mm = re.search(r"\b(\w+)(?:\[[^\]]*\])?;\s*$", st)
            name = mm.group(1) if mm and not st.startswith("[") else None
            if name in docs and not st.startswith("//"):
                out.append("        /// <summary>%s</summary>" % xml_escape(docs[name]))
            out.extend(pending)
            pending = []
            out.append(ln)
        j += 1
    out.extend(pending)
    out.extend(lines[j:])
    return out


def post_process(args, work, raw, staged, topics, model):
    out_root = os.path.abspath(args.output)
    os.makedirs(out_root, exist_ok=True)
    by_type = {e["type"]: e for e in topics}
    name_map = parse_name_map(args.namespace_map)
    emitted_topics = set()

    # collect raw files
    raw_files = []
    for root, dirs, files in os.walk(raw):
        dirs.sort()
        for f in sorted(files):
            if f.endswith(".cs"):
                raw_files.append(os.path.relpath(os.path.join(root, f), raw).replace(os.sep, "/"))
    parsed = {}
    for rel in raw_files:
        with open(os.path.join(raw, *rel.split("/")), encoding="utf-8", newline="") as f:
            parsed[rel] = split_blocks(f.read().replace("\r\n", "\n"))

    # --only: keep the listed types and everything they depend on
    keep = None
    if args.only:
        keep = only_closure(parsed, [x.strip() for x in args.only.split(",") if x.strip()])

    # remove stale files of an earlier run
    if os.path.isdir(out_root):
        for root, dirs, files in os.walk(out_root):
            for f in files:
                if f.endswith(".cs"):
                    p = os.path.join(root, f)
                    with open(p, encoding="utf-8", errors="replace") as fh:
                        if MARKER in fh.read(400):
                            os.remove(p)

    written = []
    for rel in raw_files:
        pre, namespaces = parsed[rel]
        idl_rel = rel[:-3] + ".idl"
        if idl_rel not in staged:
            raise ImportError_("importer produced %s but no staged IDL %s exists" % (rel, idl_rel))
        header = [
            "// <auto-generated>",
            "// Generated by %s v%s (scripts/legacy-idl-import.py) from CycloneDDS.IdlImporter output." % (MARKER, VERSION),
            "// Source IDL: %s  (SHA-256 of the original file: %s)" % (idl_rel, staged[idl_rel]["orig_sha"]),
            "// Do not edit - re-run the script when the IDL changes (DD-013).",
            "// </auto-generated>",
        ]
        body_pre = []
        in_header = True
        for ln in pre:
            if in_header and (ln.startswith("//") or ln.strip() == ""):
                if ln.startswith("// </auto-generated>"):
                    in_header = False
                continue
            in_header = False
            body_pre.append(ln)
        while body_pre and body_pre[0].strip() == "":
            body_pre.pop(0)
        out_lines = header + [""] + body_pre
        while out_lines and out_lines[-1].strip() == "":
            out_lines.pop()
        out_lines.append("")
        any_type = False
        for ns in namespaces:
            types_out = []
            for t in ns["types"]:
                scoped = ns["ns"].replace(".", "::") + "::" + t["name"]
                if keep is not None and scoped not in keep:
                    continue
                lines = list(t["lines"])
                entry = by_type.get(scoped)
                if t["kind"] == "struct":
                    lines = fix_topic(lines, scoped, entry, args.topic_name)
                    if entry is not None:
                        emitted_topics.add(scoped)
                if args.doc_comments:
                    lines = add_member_docs(lines, docs_for(model, scoped))
                types_out.append(lines)
            if not types_out:
                continue
            any_type = True
            out_lines.append("namespace %s" % ns["ns"])
            out_lines.append("{")
            for k, tl in enumerate(types_out):
                out_lines.extend(tl)
                out_lines.append("")
            out_lines.append("}")
            out_lines.append("")
        if not any_type:
            continue
        text = "\n".join(out_lines)
        if name_map:
            text = apply_name_map_cs(text, name_map)
        dest = os.path.join(out_root, *rel.split("/"))
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        with open(dest, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        written.append(rel)
    missing = [e["type"] for e in topics if e["type"] not in emitted_topics and (keep is None or e["type"] in keep)]
    if missing:
        raise ImportError_("keylist types missing from the importer output: %s" % ", ".join(missing))
    return written


def fix_topic(lines, scoped, entry, policy):
    """Rewrites the attribute block of a struct: [DdsTopic("n")] (+ [DdsQos]) as the table demands."""
    t_idx = next(k for k, ln in enumerate(lines) if TYPE_RE.match(ln))
    attrs, rest = lines[:t_idx], lines[t_idx:]
    is_topic = any(a.strip().startswith("[DdsTopic(") for a in attrs)
    new_attrs = []
    if not is_topic and entry is None:
        return lines
    name = topic_name(policy, scoped)
    for a in attrs:
        s = a.strip()
        if s.startswith("[DdsTopic("):
            new_attrs.append('    [DdsTopic("%s")]' % name)
        elif s.startswith("[DdsStruct]"):
            new_attrs.append('    [DdsTopic("%s")] // I-3: keylist without keys is a topic' % name)
        elif s.startswith("[DdsQos("):
            continue
        else:
            new_attrs.append(a)
    q = qos_attr(entry["qos"]) if entry else None
    if q:
        # right after the DdsTopic line
        idx = next(k for k, a in enumerate(new_attrs) if a.strip().startswith("[DdsTopic("))
        new_attrs.insert(idx + 1, "    %s // I-2: from '#pragma topic'" % q)
    return new_attrs + rest


def only_closure(parsed, only):
    allt = {}
    for rel, (pre, nss) in parsed.items():
        for ns in nss:
            for t in ns["types"]:
                allt[ns["ns"].replace(".", "::") + "::" + t["name"]] = t
    roots = []
    for o in only:
        cands = [k for k in allt if k == o or k.endswith("::" + o)]
        if not cands:
            raise ImportError_("--only: no type named %s" % o)
        roots.extend(cands)
    keep, todo = set(), list(roots)
    while todo:
        n = todo.pop()
        if n in keep:
            continue
        keep.add(n)
        body = "\n".join(allt[n]["lines"])
        for other in allt:
            if other in keep:
                continue
            dotted = other.replace("::", ".")
            simple = other.split("::")[-1]
            if re.search(r"(?<![\w.])" + re.escape(dotted) + r"(?!\w)", body) or (
                    same_ns(n, other) and re.search(r"(?<![\w.])" + re.escape(simple) + r"(?!\w)", body)):
                todo.append(other)
    return keep


def same_ns(a, b):
    return a.rsplit("::", 1)[0] == b.rsplit("::", 1)[0]


def parse_name_map(items):
    res = []
    for it in items or []:
        if "=" not in it:
            raise ImportError_("--namespace-map expects A=B, got %r" % it)
        a, b = it.split("=", 1)
        res.append((a.strip().replace(".", "::"), b.strip().replace(".", "::")))
    return res


def apply_name_map_cs(text, name_map):
    for a, b in name_map:
        ad, bd = a.replace("::", "."), b.replace("::", ".")
        text = re.sub(r"(?<![\w.])" + re.escape(ad) + r"(?=[.\s])", bd, text)
    return text


# ----------------------------------------------------------------------------------------------
# Step 5: verify
# ----------------------------------------------------------------------------------------------

CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <NoWarn>$(NoWarn);CS8981;CS0649</NoWarn>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="CycloneDDS.NET" Version="%s" />
    <Compile Include="gen/**/*.cs" />
  </ItemGroup>
</Project>
"""


def verify(args, work, out_root, topics, model, written, tools):
    vdir = os.path.join(work, "verify")
    if os.path.isdir(vdir):
        shutil.rmtree(vdir)
    gen = os.path.join(vdir, "gen")
    shutil.copytree(os.path.abspath(out_root), gen, ignore=shutil.ignore_patterns("*.idl"))
    with open(os.path.join(vdir, "verify.csproj"), "w", encoding="utf-8", newline="\n") as f:
        f.write(CSPROJ % args.package_version)
    if args.package_source:
        with open(os.path.join(vdir, "nuget.config"), "w", encoding="utf-8", newline="\n") as f:
            f.write('<configuration><packageSources><clear/><add key="legacy-idl-import" value="%s"/></packageSources></configuration>\n'
                    % os.path.abspath(args.package_source))
    env = tool_env(tools)
    p = subprocess.run([find_dotnet(), "build", "-v", "q", "-nologo", "-clp:NoSummary", os.path.join(vdir, "verify.csproj")],
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=env, universal_newlines=True, cwd=vdir)
    with open(os.path.join(work, "verify-build.log"), "w", encoding="utf-8", newline="\n") as f:
        f.write(p.stdout)
    if p.returncode != 0:
        tail = "\n".join(p.stdout.strip().split("\n")[-25:])
        return ["BUILD: the generated C# does not compile / the code generator failed:\n" + tail], []
    gdir = os.path.join(vdir, "obj")
    idls = []
    for root, dirs, files in os.walk(gdir):
        if os.path.basename(root) == "CycloneDdsGenerated":
            for f in sorted(files):
                if f.endswith(".idl"):
                    idls.append(os.path.join(root, f))
    if not idls:
        return ["BUILD: the code generator emitted no IDL (no CycloneDdsGenerated/*.idl under obj/)"], []
    gmodel = Model()
    topic_flags = {}
    for path in sorted(idls):
        with open(path, encoding="utf-8") as f:
            text = f.read()
        parser = parse_idl_text(text, os.path.relpath(path, vdir))
        gmodel.add(parser)
        # '@topic' marks topics in the generated IDL
        for d in parser.decls:
            if d["kind"] == "struct" and "topic" in d["ann"]:
                topic_flags[sc(d["name"])] = True
    name_map = parse_name_map(args.namespace_map)
    exp_keys, key_diffs, key_notes = effective_keys(topics, model)
    expected = model.normalised(exp_keys, name_map)
    got = gmodel.normalised()
    emitted = emitted_type_names(out_root, written)
    diffs = compare_models(expected, got, emitted if args.only else None) + key_diffs
    notes = list(key_notes)
    table = {map_scoped(e["type"], name_map): e for e in topics}
    # topics: @topic present, keys, qos, name
    cs_text = {}
    for rel in written:
        with open(os.path.join(out_root, *rel.split("/")), encoding="utf-8") as f:
            cs_text[rel] = f.read()
    cs_blocks = {}
    for rel, text in cs_text.items():
        pre, nss = split_blocks(text)
        for ns in nss:
            for t in ns["types"]:
                cs_blocks[ns["ns"].replace(".", "::") + "::" + t["name"]] = t["lines"]
    for scoped, e in sorted(table.items()):
        if args.only and scoped not in emitted:
            continue
        if scoped not in topic_flags:
            diffs.append("%s: not a topic in the generated IDL (missing [DdsTopic])" % scoped)
        blk = cs_blocks.get(scoped)
        if blk is None:
            diffs.append("%s: missing from the generated C#" % scoped)
            continue
        attrs = [ln.strip() for ln in blk[: next(k for k, ln in enumerate(blk) if TYPE_RE.match(ln))]]
        tn = [a for a in attrs if a.startswith("[DdsTopic(")]
        orig_name = next((k for k, v in ((e["type"], e) for e in topics) if map_scoped(k, name_map) == scoped), scoped)
        want = '[DdsTopic("%s")]' % topic_name(args.topic_name, orig_name)
        if not tn or not tn[0].startswith(want):
            diffs.append("%s: topic attribute %s, expected %s" % (scoped, tn[0] if tn else "missing", want))
        qa = [a for a in attrs if a.startswith("[DdsQos(")]
        want_q = expected_qos_props(e["qos"])
        if want_q:
            if not qa:
                diffs.append("%s: [DdsQos] missing, expected %s" % (scoped, want_q))
            else:
                got_q = dict(re.findall(r"(\w+)\s*=\s*([\w.]+)", qa[0].split("//")[0]))
                if got_q != want_q:
                    diffs.append("%s: [DdsQos] %s != table %s" % (scoped, got_q, want_q))
        elif qa:
            diffs.append("%s: [DdsQos] present but the table has no policies" % scoped)
        # key order note
        gm = got.get(scoped)
        ek = exp_keys.get(e["type"], [])
        if gm and ek and gm["keys"] != ek and sorted(gm["keys"]) == sorted(ek):
            notes.append("NOTE %s: keylist order %s differs from member order %s (C# keys follow declaration order)"
                         % (scoped, ek, gm["keys"]))
    return diffs, notes


def effective_keys(topics, model):
    """Keys as C# can express them: a dotted key 'a.b' becomes the whole member 'a' ([DdsKey] is per member).
    That is exact when b is all of a's struct, otherwise the key is widened - reported as a difference (I-9)."""
    keys, diffs, notes = {}, [], []
    for e in topics:
        d = model.decls[e["type"]]
        out = []
        for k in e["keys"]:
            top = k.split(".")[0]
            if "." in k:
                m = next(m for m in d["members"] if m["name"] == top)
                t = m["type"]
                sd = model.decls.get(model.resolve(t[1], d["scope"])) if t[0] == "named" else None
                if sd is not None and sd["kind"] == "struct" and len(sd["members"]) == 1 and not m["dims"]:
                    notes.append("NOTE %s: key %s mapped to whole member %s (identical: the struct has one member)" % (e["type"], k, top))
                else:
                    diffs.append("%s: key %s widened to the whole member %s (nested key paths are not expressible in C#)" % (e["type"], k, top))
            if top not in out:
                out.append(top)
        keys[e["type"]] = out
    return keys, diffs, notes


def emitted_type_names(out_root, written):
    names = set()
    for rel in written:
        with open(os.path.join(out_root, *rel.split("/")), encoding="utf-8") as f:
            pre, nss = split_blocks(f.read())
        for ns in nss:
            for t in ns["types"]:
                names.add(ns["ns"].replace(".", "::") + "::" + t["name"])
    return names


def compare_models(expected, got, only_set):
    diffs = []
    names = sorted(set(expected) | set(got))
    for n in names:
        if only_set is not None and n not in only_set:
            continue
        a, b = expected.get(n), got.get(n)
        if a is None:
            diffs.append("%s: only in the generated IDL" % n)
            continue
        if b is None:
            diffs.append("%s: missing from the generated IDL" % n)
            continue
        if a["kind"] != b["kind"]:
            diffs.append("%s: kind %s != %s" % (n, a["kind"], b["kind"]))
            continue
        if a["kind"] == "enum":
            if a["bit_bound"] != b["bit_bound"]:
                diffs.append("%s: bit_bound %s != %s" % (n, a["bit_bound"], b["bit_bound"]))
            if a["values"] != b["values"]:
                diffs.append("%s: enumerators %s != %s" % (n, a["values"], b["values"]))
            continue
        if a["ext"] != b["ext"]:
            diffs.append("%s: extensibility %s != %s" % (n, a["ext"], b["ext"]))
        if a["kind"] == "struct":
            am = [m["name"] for m in a["members"]]
            bm = [m["name"] for m in b["members"]]
            if am != bm:
                diffs.append("%s: members %s != %s" % (n, am, bm))
            else:
                for x, y in zip(a["members"], b["members"]):
                    for f in ("type", "optional"):
                        if x[f] != y[f]:
                            diffs.append("%s.%s: %s %r != %r" % (n, x["name"], f, x[f], y[f]))
                    if x["id"] is not None and x["id"] != y["id"]:
                        diffs.append("%s.%s: id %r != %r" % (n, x["name"], x["id"], y["id"]))
            if sorted(a["keys"]) != sorted(b["keys"]):
                diffs.append("%s: keys %s != %s" % (n, a["keys"], b["keys"]))
        else:
            if a["disc"] != b["disc"]:
                diffs.append("%s: discriminator %s != %s" % (n, a["disc"], b["disc"]))
            if a["cases"] != b["cases"]:
                diffs.append("%s: cases %s != %s" % (n, json.dumps(a["cases"]), json.dumps(b["cases"])))
    return diffs


# ----------------------------------------------------------------------------------------------
# main
# ----------------------------------------------------------------------------------------------

def build_parser():
    ap = argparse.ArgumentParser(
        prog="legacy-idl-import",
        description="Import legacy (keylist-pragma) IDL into CycloneDDS.NET C# (DD-013). Inputs are copied to --work and "
                    "never modified. Folder layout: every input/include folder is copied to <work>/src/<folder name>, "
                    "so '#include \"..\\Other\\X.idl\"' between sibling folders keeps working.")
    ap.add_argument("--input", nargs="+", required=True, help="IDL files or folders (the master IDLs to import)")
    ap.add_argument("--include-dir", nargs="*", default=[], help="folders holding IDL that the inputs include (imported too)")
    ap.add_argument("--work", required=True, help="work folder (cleaned IDL, topics.json, raw importer output, verify project)")
    ap.add_argument("--output", required=True, help="folder receiving the post-processed C#")
    ap.add_argument("--tools", default=os.environ.get("LEGACY_IDL_TOOLS"),
                    help="folder with idlc and CycloneDDS.IdlImporter.dll (the NuGet package's tools/); env LEGACY_IDL_TOOLS")
    ap.add_argument("--namespace-map", action="append", default=[], metavar="A=B",
                    help="rename C# namespace A to B (changes XTypes type names - peers matching by type name will not match)")
    ap.add_argument("--topic-name", choices=TOPIC_NAME_POLICIES, default="scoped",
                    help="topic name of [DdsTopic]: scoped A::B::T (default), underscore A_B_T, bare T, typename A.B.T")
    ap.add_argument("--only", help="comma-separated type names; keep only these and the types they depend on")
    ap.add_argument("--doc-comments", action="store_true", help="copy trailing IDL member comments as /// docs (I-7)")
    ap.add_argument("--keep-work", action="store_true", help="do not delete the verify project's bin/obj afterwards")
    ap.add_argument("--verify", action="store_true", help="build the output and compare the code generator's IDL with the input")
    ap.add_argument("--package-source", default=os.environ.get("LEGACY_IDL_PACKAGE_SOURCE"),
                    help="NuGet source (folder or URL) for CycloneDDS.NET during --verify; default: the machine's sources")
    ap.add_argument("--package-version", default="1.2.20", help="CycloneDDS.NET version used by --verify")
    return ap


def main(argv=None):
    args = build_parser().parse_args(argv)
    try:
        if not args.tools:
            raise ImportError_("--tools (or env LEGACY_IDL_TOOLS) is required")
        tools = os.path.abspath(args.tools)
        work = os.path.abspath(args.work)
        os.makedirs(work, exist_ok=True)
        staged, masters = stage(args, work)
        topics, model = parse_pragmas(staged, work)
        with open(os.path.join(work, "topics.json"), "w", encoding="utf-8", newline="\n") as f:
            json.dump(topic_table_json(topics), f, indent=2, sort_keys=True)
            f.write("\n")
        raw = run_importer(args, work, staged, masters, tools)
        written = post_process(args, work, raw, staged, topics, model)
        print("legacy-idl-import: %d topics, %d C# files written to %s" % (len(topics), len(written), args.output))
        if args.verify:
            diffs, notes = verify(args, work, args.output, topics, model, written, tools)
            for n in notes:
                print(n)
            if diffs:
                print("VERIFY FAILED: %d difference(s)" % len(diffs))
                for d in diffs:
                    print("  DIFF " + d)
                return 1
            print("VERIFY OK: %d types compared; generated IDL equals the cleaned input type by type; QoS, topic names and keys match the table"
                  % len(model.normalised()))
        if not args.keep_work:
            for sub in ("bin", "obj"):
                shutil.rmtree(os.path.join(work, "verify", sub), ignore_errors=True)
        return 0
    except ImportError_ as e:
        print("legacy-idl-import: error: %s" % e, file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
