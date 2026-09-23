"""Offline boundary regressions for the existing desktop-result evaluator."""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

from PIL import Image, ImageDraw


spec = importlib.util.spec_from_file_location(
    "sustained_desktop", Path(__file__).parents[1] / "scripts/Measure-SustainedDesktop.py")
evaluator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(evaluator)


class ScrollResultBoundaries(unittest.TestCase):
    def evaluate(self, *, response=1086, next_input=1093, stale_at=None,
                 reviewed=True, accumulated=1):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "desktop").mkdir()

            def write(name, value):
                (root / name).write_text(json.dumps(value), encoding="utf-8")

            hashes = {}
            for index, name in enumerate(("old", "expected", "next")):
                image = Image.new("RGB", (100, 80), (20, 20, 20))
                ImageDraw.Draw(image).rectangle((5 + index * 20, 10, 12 + index * 20, 35), fill="white")
                image.save(root / "desktop" / (name + ".png"))
                hashes[name] = hashlib.sha256(image.crop((0, 0, 76, 56)).tobytes()).hexdigest()

            write("plan.json", {"frequency": 1000, "source": "synthetic-evaluator-fixture",
                                "scrollProfile": "continuous", "condition": "cold"})
            inputs = [{"index": index, "axis": "vertical", "apiArgument": -1, "detents": -1,
                       "begin": begin, "actualMs": begin, "dueMs": begin}
                      for index, begin in enumerate((1000, next_input))]
            (root / "driver.jsonl").write_text("\n".join(
                json.dumps({"kind": "scroll-input", "detail": command}) for command in inputs))
            trace = []
            for index, command in enumerate(inputs):
                begin = command["begin"]
                trace.extend([
                    {"kind": "sheet-wheel", "ticks": begin + 2, "data": {
                        "handlerEntered": begin + 1, "horizontal": False,
                        "priorHorizontal": 0, "priorVertical": index * 90,
                        "wheelVertical": (index + 1) * 90}},
                    {"kind": "scroll-view-changed", "ticks": begin + 5, "data": {
                        "state": {"horizontalOffset": 0, "verticalOffset": (index + 1) * 90}}}])
            (root / "app-trace.jsonl").write_text("\n".join(json.dumps(event) for event in trace))
            updates = [(950, "old"), (response, "expected"), (next_input + 94, "next")]
            if stale_at is not None:
                updates.append((stale_at, "next"))
            write("desktop/frames.json", [
                {"present": tick, "acquired": tick + 1, "copied": tick + 2,
                 "accumulated": accumulated, "masked": False, "path": name + ".png"}
                for tick, name in sorted(updates)])
            write("desktop/lifetime.json", {"complete": True, "saturated": False})
            reviews = {hashes["next"]: {"frame": "next.png", "state": "0.00,90.00", "correct": False}}
            if reviewed:
                reviews[hashes["expected"]] = {
                    "frame": "expected.png", "state": "0.00,90.00", "correct": True}
            write("desktop-content-review.json", reviews)
            return evaluator.analyze(root)["commands"][0]

    def test_next_request_does_not_invalidate_an_already_readable_response(self):
        result = self.evaluate()
        self.assertEqual(result["expectedAtMs"], 86)
        self.assertEqual(result["status"], "PASS")

    def test_first_correct_response_after_deadline_is_still_failure(self):
        result = self.evaluate(response=1101)
        self.assertEqual(result["expectedAtMs"], 101)
        self.assertEqual(result["status"], "FAIL")

    def test_wrong_result_before_another_request_is_still_failure(self):
        result = self.evaluate(next_input=1250, stale_at=1110)
        self.assertEqual(result["status"], "FAIL")

    def test_missing_content_review_cannot_pass(self):
        self.assertEqual(self.evaluate(reviewed=False)["status"], "INCONCLUSIVE")

    def test_missed_desktop_updates_cannot_pass(self):
        self.assertEqual(self.evaluate(accumulated=2)["status"], "INCONCLUSIVE")


if __name__ == "__main__":
    unittest.main()
