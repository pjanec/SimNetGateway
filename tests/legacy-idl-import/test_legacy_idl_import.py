"""Tests of scripts/legacy-idl-import.py (DD-013, T-028) on SYNTHETIC IDL fixtures (invented names, never the proprietary IDL).

The pipeline tests need the CycloneDDS.NET tools folder (idlc + CycloneDDS.IdlImporter.dll, from the NuGet package's tools/) in
LEGACY_IDL_TOOLS and a NuGet source holding CycloneDDS.NET (LEGACY_IDL_PACKAGE_SOURCE, optional when nuget.org is reachable);
without LEGACY_IDL_TOOLS they are skipped (the pure parser tests still run). Run: bash scripts/legacy-idl-import-test.sh
"""
import contextlib
import importlib.util
import io
import json
import os
import re
import shutil
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
FIXTURES = os.path.join(HERE, "fixtures")
SCRIPT = os.path.join(HERE, "..", "..", "scripts", "legacy-idl-import.py")
TOOLS = os.environ.get("LEGACY_IDL_TOOLS")
SOURCE = os.environ.get("LEGACY_IDL_PACKAGE_SOURCE")

spec = importlib.util.spec_from_file_location("legacy_idl_import", SCRIPT)
lii = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lii)

# type -> (keys, reliability, durability, history, depth, has_topic_pragma)
EXPECTED_TABLE = {
    "Acme::Sys::Track": ([ "Id"], "best_effort", "volatile", "keep_last", 1, True),
    "Acme::Sys::Link": (["From", "To"], "reliable", "transient_local", "keep_last", 2, True),
    "Acme::Sys::Pair": (["B", "A"], None, None, None, None, False),
    "Acme::Sys::Alarm": ([], "reliable", "volatile", "keep_all", 1, True),
    "Acme::Sys::Ping": ([], None, None, None, None, True),
    "Acme::Sys::Bare": (["A", "B", "C"], None, None, None, None, False),
    "Acme::Sys::Cfg": ([], "best_effort", "persistent", "keep_last", 7, True),
    "Acme::Sys::Owned": (["Node.Index"], "reliable", "transient_local", "keep_last", 1, True),
    "Acme::Sys::Wrapper": (["Id"], "reliable", None, None, None, True),
    "Shared::StampedRef": (["Id"], "reliable", "transient_local", "keep_last", 3, True),
}

EXPECTED_QOS = {   # the [DdsQos] properties each topic must carry (empty = no attribute)
    "Acme::Sys::Track": {"Reliability": "DdsReliability.BestEffort", "Durability": "DdsDurability.Volatile",
                         "HistoryKind": "DdsHistoryKind.KeepLast", "HistoryDepth": "1"},
    "Acme::Sys::Link": {"Reliability": "DdsReliability.Reliable", "Durability": "DdsDurability.TransientLocal",
                        "HistoryKind": "DdsHistoryKind.KeepLast", "HistoryDepth": "2"},
    "Acme::Sys::Pair": {},
    "Acme::Sys::Alarm": {"Reliability": "DdsReliability.Reliable", "Durability": "DdsDurability.Volatile",
                         "HistoryKind": "DdsHistoryKind.KeepAll"},
    "Acme::Sys::Ping": {},
    "Acme::Sys::Bare": {},
    "Acme::Sys::Cfg": {"Reliability": "DdsReliability.BestEffort", "Durability": "DdsDurability.Persistent",
                       "HistoryKind": "DdsHistoryKind.KeepLast", "HistoryDepth": "7"},
    "Acme::Sys::Wrapper": {"Reliability": "DdsReliability.Reliable"},
    "Shared::StampedRef": {"Reliability": "DdsReliability.Reliable", "Durability": "DdsDurability.TransientLocal",
                           "HistoryKind": "DdsHistoryKind.KeepLast", "HistoryDepth": "3"},
}
EXPECTED_KEY_MEMBERS = {   # [DdsKey] members in the C# (a dotted keylist key becomes its whole member)
    "Acme::Sys::Track": ["Id"], "Acme::Sys::Link": ["From", "To"], "Acme::Sys::Pair": ["A", "B"],
    "Acme::Sys::Alarm": [], "Acme::Sys::Ping": [], "Acme::Sys::Bare": ["A", "B", "C"], "Acme::Sys::Cfg": [],
    "Acme::Sys::Owned": ["Node"], "Acme::Sys::Wrapper": ["Id"], "Shared::StampedRef": ["Id"],
}


def make_crlf_fixtures(dest):
    """Copies the (LF) fixtures to dest with CRLF line endings, like the originals."""
    for folder in ("Acme", "Shared"):
        for name in os.listdir(os.path.join(FIXTURES, folder)):
            with open(os.path.join(FIXTURES, folder, name), "rb") as f:
                data = f.read().replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
            os.makedirs(os.path.join(dest, folder), exist_ok=True)
            with open(os.path.join(dest, folder, name), "wb") as f:
                f.write(data)


def run_main(argv):
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        code = lii.main(argv)
    return code, out.getvalue(), err.getvalue()


def tree(root):
    res = {}
    for r, dirs, files in os.walk(root):
        for f in files:
            p = os.path.join(r, f)
            with open(p, "rb") as fh:
                res[os.path.relpath(p, root).replace(os.sep, "/")] = fh.read()
    return res


def read_blocks(out_root):
    blocks = {}
    for rel, data in tree(out_root).items():
        if rel.endswith(".cs"):
            pre, nss = lii.split_blocks(data.decode("utf-8"))
            for ns in nss:
                for t in ns["types"]:
                    blocks[ns["ns"].replace(".", "::") + "::" + t["name"]] = "\n".join(t["lines"])
    return blocks


class ParserTests(unittest.TestCase):
    """Pure parser tests - no tools needed."""

    def qos(self, *lines):
        q = {}
        for ln in lines:
            lii.parse_topic_pragma(ln.split(), q, "t.idl:1")
        return q

    def test_one_policy_per_line(self):
        q = self.qos("reliability best_effort", "durability transient_local", "history keep_last 4")
        self.assertEqual(q, {"reliability": "best_effort", "durability": "transient_local", "history": "keep_last", "history_depth": 4})

    def test_compact_spelling(self):
        self.assertEqual(self.qos("reliable transient_local keep_last 1"),
                         {"reliability": "reliable", "durability": "transient_local", "history": "keep_last", "history_depth": 1})
        self.assertEqual(self.qos("best_effort volatile keep_all 1")["history"], "keep_all")
        self.assertEqual(self.qos("reliable persistent keep_last 9")["durability"], "persistent")

    def test_bare_pragma_has_no_policies(self):
        self.assertEqual(self.qos(""), {})

    def test_unknown_word_names_the_location(self):
        with self.assertRaises(lii.ImportError_) as cm:
            lii.parse_topic_pragma(["reliability", "sometimes"], {}, "x.idl:7")
        self.assertIn("x.idl:7", str(cm.exception))
        with self.assertRaises(lii.ImportError_):
            lii.parse_topic_pragma(["frobnicate"], {}, "x.idl:8")

    def test_compact_and_long_form_agree(self):
        a = self.qos("reliable volatile keep_last 2")
        b = self.qos("reliability reliable", "durability volatile", "history keep_last 2")
        self.assertEqual(a, b)

    def test_topic_names(self):
        self.assertEqual(lii.topic_name("scoped", "A::B::T"), "A::B::T")
        self.assertEqual(lii.topic_name("underscore", "A::B::T"), "A_B_T")
        self.assertEqual(lii.topic_name("bare", "A::B::T"), "T")
        self.assertEqual(lii.topic_name("typename", "A::B::T"), "A.B.T")

    def test_normalise_text(self):
        t = lii.normalise_text(b'\xef\xbb\xbf#include "..\\X\\Y.idl"\r\nstruct S {};\r\n')
        self.assertNotIn("\r", t)
        self.assertIn('"../X/Y.idl"', t)

    def test_model_resolves_typedefs_and_flattens_arrays(self):
        text = "module M { typedef long Row[4]; typedef octet G[16]; struct S { Row grid[3]; G g; sequence<Row> q; }; };"
        m = lii.Model()
        m.add(lii.parse_idl_text(text, "t.idl"))
        mem = {x["name"]: x["type"] for x in m.normalised()["M::S"]["members"]}
        self.assertEqual(mem["grid"], "int32[3][4]")
        self.assertEqual(mem["g"], "octet[16]")

    def test_comparator_reports_differences(self):
        def model(t):
            m = lii.Model()
            m.add(lii.parse_idl_text(t, "t.idl"))
            return m.normalised()
        base = "module M { enum E { A, B }; struct S { long x; E e; }; union U switch (E) { case A: long a; case B: double b; }; };"
        self.assertEqual(lii.compare_models(model(base), model(base), None), [])
        for mutated in (base.replace("long x", "short x"), base.replace("A, B", "A, @value(7) B"),
                        base.replace("case B: double b", "case B: float b"), base.replace("struct S", "@mutable struct S"),
                        base.replace("enum E", "@bit_bound(8) enum E"), base.replace("long x;", "@key long x;")):
            self.assertTrue(lii.compare_models(model(base), model(mutated), None), mutated)

    def test_unknown_pragma_word_fails_with_file_and_line(self):
        d = tempfile.mkdtemp()
        try:
            os.makedirs(os.path.join(d, "In"))
            with open(os.path.join(d, "In", "Bad.idl"), "w") as f:
                f.write("module M {\n  struct S { long a; };\n  #pragma keylist S a\n  #pragma topic reliable frobnicate\n};\n")
            code, out, err = run_main(["--input", os.path.join(d, "In"), "--work", os.path.join(d, "w"),
                                       "--output", os.path.join(d, "o"), "--tools", d])
            self.assertEqual(code, 2)
            self.assertIn("In/Bad.idl:4", err)
            self.assertIn("frobnicate", err)
            with open(os.path.join(d, "In", "Bad.idl"), "w") as f:
                f.write("module M {\n  struct S { long a; };\n  #pragma keylist S a\n  #pragma weird\n};\n")
            code, out, err = run_main(["--input", os.path.join(d, "In"), "--work", os.path.join(d, "w"),
                                       "--output", os.path.join(d, "o"), "--tools", d])
            self.assertEqual(code, 2)
            self.assertIn("In/Bad.idl:4", err)
        finally:
            shutil.rmtree(d, ignore_errors=True)

    def test_bare_topic_pragma_may_precede_its_keylist(self):
        d = tempfile.mkdtemp()
        try:
            os.makedirs(os.path.join(d, "In"))
            with open(os.path.join(d, "In", "P.idl"), "w") as f:
                f.write("module M {\n  struct S { long a; };\n  #pragma topic\n  #pragma keylist S a\n"
                        "  #pragma topic durability volatile\n  struct T { long b; };\n  #pragma keylist T\n};\n")
            args = lii.build_parser().parse_args(["--input", os.path.join(d, "In"), "--work", d, "--output", d])
            staged, masters = lii.stage(args, d)
            topics, _ = lii.parse_pragmas(staged, d)
            tbl = {e["type"]: e for e in topics}
            self.assertTrue(tbl["M::S"]["has_topic_pragma"])
            self.assertEqual(tbl["M::S"]["qos"]["durability"], "volatile")
            self.assertFalse(tbl["M::T"]["has_topic_pragma"])
        finally:
            shutil.rmtree(d, ignore_errors=True)


@unittest.skipUnless(TOOLS, "LEGACY_IDL_TOOLS not set (folder with idlc + CycloneDDS.IdlImporter.dll)")
class PipelineTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.mkdtemp(prefix="lii-")
        cls.src = os.path.join(cls.tmp, "orig")
        make_crlf_fixtures(cls.src)
        cls.work, cls.out = os.path.join(cls.tmp, "work"), os.path.join(cls.tmp, "out")
        cls.code, cls.stdout, cls.stderr = run_main(cls.argv(cls.work, cls.out, "--verify", "--keep-work"))
        cls.blocks = read_blocks(cls.out)

    @classmethod
    def argv(cls, work, out, *extra):
        a = ["--input", os.path.join(cls.src, "Acme"), "--include-dir", os.path.join(cls.src, "Shared"),
             "--work", work, "--output", out, "--tools", TOOLS]
        if SOURCE:
            a += ["--package-source", SOURCE]
        return a + list(extra)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def test_run_and_verify_succeed(self):
        self.assertEqual(self.code, 0, self.stdout + self.stderr)
        self.assertIn("VERIFY OK", self.stdout)

    def test_staged_idl_is_clean(self):
        for rel, data in tree(os.path.join(self.work, "src")).items():
            text = data.decode("utf-8")
            self.assertNotIn("\r", text, rel)
            self.assertNotIn("#pragma topic", text, rel)
            for ln in text.split("\n"):
                if ln.lstrip().startswith("#include"):
                    self.assertNotIn("\\", ln, rel)
        acme = tree(os.path.join(self.work, "src"))["Acme/Acme.Track.idl"].decode()
        self.assertIn("#pragma keylist Link From To", acme)          # keylists are kept
        self.assertIn("#include \"../Shared/Shared.idl\"", acme)

    def test_topic_table_is_exact(self):
        with open(os.path.join(self.work, "topics.json"), encoding="utf-8") as f:
            table = json.load(f)
        got = {e["type"]: (e["keys"], e["reliability"], e["durability"], e["history"], e["history_depth"], e["has_topic_pragma"])
               for e in table}
        self.assertEqual(got, EXPECTED_TABLE)
        for e in table:
            self.assertTrue(e["source"].endswith(".idl"))
            self.assertGreater(e["line"], 0)

    def test_every_keylist_type_is_a_topic_with_the_right_keys(self):
        for t, keys in EXPECTED_KEY_MEMBERS.items():
            blk = self.blocks[t]
            self.assertIn('[DdsTopic("%s")]' % t, blk, t)
            self.assertNotIn("[DdsStruct]", blk, t)
            members = re.findall(r"\[DdsKey\]\s*\n\s*public [^\n]*? (\w+)(?:\[\d+\])?;", blk)
            self.assertEqual(members, keys, t)

    def test_non_keylist_structs_stay_plain(self):
        for t in ("Shared::Vec3", "Shared::Pose", "Acme::Sys::Payload"):
            self.assertNotIn("[DdsTopic", self.blocks[t], t)

    def test_keyless_topic_carries_the_workaround_tag(self):
        self.assertIn("I-3", self.blocks["Acme::Sys::Ping"])
        self.assertNotIn("I-3", self.blocks["Acme::Sys::Track"])

    def test_qos_matches_the_pragmas(self):
        for t, want in EXPECTED_QOS.items():
            m = re.search(r"\[DdsQos\(([^)]*)\)\]", self.blocks[t])
            if not want:
                self.assertIsNone(m, t)
            else:
                self.assertIsNotNone(m, t)
                self.assertEqual(dict(re.findall(r"(\w+)\s*=\s*([\w.]+)", m.group(1))), want, t)

    def test_constructs_survive(self):
        b = self.blocks
        self.assertIn("[DdsUnion]", b["Acme::Sys::Payload"])
        self.assertIn("[DdsCase(0, 11)]", b["Acme::Sys::Payload"])          # multi-label case
        self.assertIn("[DdsBound(4)]", b["Acme::Sys::Track"])
        self.assertIn("[MaxLength(16)]", b["Acme::Sys::Track"])
        self.assertIn("List<string> Notes", b["Acme::Sys::Track"])
        self.assertIn("List<Shared.Vec3> Path", b["Acme::Sys::Track"])
        self.assertIn("public fixed byte Guid[16]", b["Acme::Sys::Owned"])   # typedef octet X[16]
        self.assertIn("public fixed byte Label[8]", b["Acme::Sys::Owned"])   # typedef char N[8] used as a fixed string
        self.assertIn("[DdsChar]", b["Acme::Sys::Owned"])
        self.assertIn("enum Mode : byte", b["Shared::Mode"] if "Shared::Mode" in b else "")
        self.assertIn("MODE_ON = 5", b["Shared::Mode"])
        self.assertIn("KIND_AIR = 10", b["Acme::Sys::Kind"])

    def test_provenance_header_and_no_date(self):
        files = [d.decode() for r, d in tree(self.out).items() if r.endswith(".cs")]
        self.assertEqual(len(files), 2)
        for text in files:
            head = "\n".join(text.split("\n")[:6])
            self.assertIn("legacy-idl-import v", head)
            self.assertRegex(head, r"SHA-256 of the original file: [0-9a-f]{64}")
            self.assertNotRegex(head, r"20\d\d-\d\d-\d\d")
            self.assertNotIn("\r", text)
        orig = lii.sha256_file(os.path.join(self.src, "Acme", "Acme.Track.idl"))   # hash of the ORIGINAL (CRLF) file
        self.assertIn(orig, tree(self.out)["Acme/Acme.Track.cs"].decode())

    def test_rerun_is_byte_identical(self):
        first = tree(self.out)
        code, out, err = run_main(self.argv(self.work, self.out))                                 # same folders
        self.assertEqual(code, 0, err)
        self.assertEqual(tree(self.out), first)
        w2, o2 = os.path.join(self.tmp, "work2"), os.path.join(self.tmp, "out2")
        code, out, err = run_main(self.argv(w2, o2))                                              # fresh folders
        self.assertEqual(code, 0, err)
        self.assertEqual(tree(o2), first)

    def test_doc_comments_topic_names_and_only(self):
        w, o = os.path.join(self.tmp, "work3"), os.path.join(self.tmp, "out3")
        code, out, err = run_main(self.argv(w, o, "--doc-comments", "--topic-name", "bare", "--only", "Link,Wrapper", "--verify"))
        self.assertEqual(code, 0, out + err)
        blocks = read_blocks(o)
        self.assertIn('[DdsTopic("Link")]', blocks["Acme::Sys::Link"])
        self.assertIn('[DdsTopic("Wrapper")]', blocks["Acme::Sys::Wrapper"])
        self.assertIn("Acme::Sys::Payload", blocks)       # dependencies come along
        self.assertIn("Acme::Sys::Kind", blocks)
        self.assertNotIn("Acme::Sys::Track", blocks)
        self.assertNotIn("Acme::Sys::Bare", blocks)
        w, o = os.path.join(self.tmp, "work4"), os.path.join(self.tmp, "out4")
        code, out, err = run_main(self.argv(w, o, "--doc-comments"))
        self.assertEqual(code, 0, err)
        self.assertIn("/// <summary>[deg]</summary>", tree(o)["Shared/Shared.cs"].decode())

    def test_key_order_note(self):
        self.assertIn("NOTE Acme::Sys::Pair: keylist order ['B', 'A']", self.stdout)


if __name__ == "__main__":
    unittest.main()
