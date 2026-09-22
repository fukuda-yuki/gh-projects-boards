# Evidence packaging corrections

Original product runs, raw archives, private native traces and classifications are unchanged. Verification first exposed schema assumptions in the new auditor (PID and baseline TID are filtered by the original extractors but omitted from their records); both failures and the corrected checks are recorded in `verification.json`.

The first Git byte comparison then detected that four generated JSON files had been staged with LF normalization before this evidence directory's byte-preserving attributes were added. Their working files and SHA256 receipts use CRLF. An ordinary subsequent `git add` reused unchanged index entries, so the first evidence commit retained this mismatch; the commit was created despite the failed byte gate. This packaging error is corrected in a following evidence-only commit, preserving Git history. Explicit renormalization under the local `-text` attributes re-stages the original bytes. The same directory recognizes CRLF as a valid line ending for whitespace review; it does not change whitespace policy for product code.

Final delivery is verified against Git-stored bytes and the artifact manifest, not only the working directory. This is an evidence-byte correction, not a new source build, product pass, performance run or change to the remaining scroll failure.
