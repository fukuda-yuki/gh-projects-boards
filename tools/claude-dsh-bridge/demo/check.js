"use strict";

// Verification the delegated run must make pass with `node demo/check.js`.
// Exit code 0 means every case passed; 1 prints the failing case and exits.
const { add, multiply } = require("./calc.js");

const cases = [
  ["add keeps working", () => add(2, 3) === 5],
  ["multiply(6, 7) === 42", () => multiply(6, 7) === 42],
  ["multiply(-3, 4) === -12", () => multiply(-3, 4) === -12],
];

let failed = 0;
for (const [name, run] of cases) {
  let ok = false;
  let detail = "";
  try {
    ok = run() === true;
  } catch (error) {
    detail = ` (${error.message})`;
  }
  if (!ok) failed += 1;
  console.log(`${ok ? "PASS" : "FAIL"} ${name}${detail}`);
}

if (failed > 0) {
  console.log(`FAILED ${failed}/${cases.length}`);
  process.exit(1);
}
console.log(`PASSED ${cases.length}/${cases.length}`);
