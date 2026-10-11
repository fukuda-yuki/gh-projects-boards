"use strict";

// Delegation target for the bridge's end-to-end check. `add` is complete;
// `multiply` is the small change a delegated DSH run is asked to implement.
// The end-to-end test copies this file into a scratch folder, so this reference
// copy stays unimplemented.
function add(a, b) {
  return a + b;
}

function multiply(a, b) {
  throw new Error("multiply is not implemented");
}

module.exports = { add, multiply };
