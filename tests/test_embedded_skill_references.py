"""Verify the fixed embedded QA skill source is a regular pinned file.

Runtime resolution never reads the working tree. The source check catches a
symlink or reparse point in the checkout before the model-free build.
"""
import hashlib
import pathlib
import stat
import subprocess
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
PREFIX = "src/CodingAssistant.Api/Skills/static-web-verification/"
PINS = {
    PREFIX + "SKILL.md": "a44462505576db34ccf61b57aa9952281828ced76402111389007c50f5f391e6",
    PREFIX + "references/review-checklist.md": "6d95a95fba86e0d733de6e5c2f598342c0cff8c48ad0dfbb48aa2199c0d42d3d",
}


def regular_source(root, relative):
    path = root
    parts = pathlib.PurePosixPath(relative).parts
    for index, part in enumerate(parts):
        path = path / part
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("linked source is not approved")
        if index < len(parts) - 1 and not stat.S_ISDIR(info.st_mode):
            raise ValueError("invalid source parent")
        if index == len(parts) - 1 and not stat.S_ISREG(info.st_mode):
            raise ValueError("not a regular source file")
    return path.read_bytes().replace(b"\r\n", b"\n")


class EmbeddedSkillReferenceTests(unittest.TestCase):
    def test_exact_regular_pins(self):
        for relative, expected in PINS.items():
            with self.subTest(relative=relative):
                tree = subprocess.check_output(
                    ["git", "ls-tree", "HEAD", "--", relative], cwd=ROOT, text=True
                ).strip()
                self.assertTrue(tree.startswith("100644 blob "), tree)
                self.assertEqual(hashlib.sha256(regular_source(ROOT, relative)).hexdigest(), expected)

    def test_linked_reference_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            root = pathlib.Path(folder)
            target = root / "target.md"
            target.write_text("fixture", encoding="utf-8")
            link = root / "linked.md"
            try:
                link.symlink_to(target)
            except (OSError, NotImplementedError):
                self.skipTest("host cannot create symlinks")
            with self.assertRaises(ValueError):
                regular_source(root, "linked.md")


if __name__ == "__main__":
    unittest.main()
