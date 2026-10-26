---
name: english-writing
description: >-
  Write ElasticLM XML docs, inline comments, test names and Preconditions/Steps/Expected remarks,
  exception messages, rules, and the README in English with the repository's terminology.
  Use when writing or editing any of these, when translating a term from the Japanese design,
  or when a review flags wording.
---

# Writing English in ElasticLM

Read this before writing. When a review corrects wording, add the general rule here in the same change; do not leave it in chat. The language policy is `.cursor/rules/language.mdc`.

## Sentence shape

- Plain, direct English. Short sentences and common words.
- XML doc elements are full sentences and end with a period (SA1629).
- Summaries start with a verb in the third person: "Returns ...", "Creates ...", "Verifies ...".
  - Properties: "Gets ..." or "Gets or sets ..." (SA1623).
  - Constructors: "Initializes a new instance of the <see cref="T"/> class." (SA1642).
  - Boolean members: "Gets a value indicating whether ...".
- Write `<see langword="true"/>`, `<see langword="false"/>`, `<see langword="null"/>` for keywords.
- Refer to types and members with `<see cref="..."/>` and parameters with `<paramref name="..."/>`.
- Inline comments explain why, not what the next line does. A measured reason names the measurement.
- Keep the subject the same across a method name, its summary, and its remarks.

## Test names and remarks

- `{Target}_{Scenario}_{Expected}` in PascalCase.
- Remarks use the headings Preconditions, Steps, Expected (`.cursor/rules/testing.mdc`). Name the actual input and the actual check.

## Exception and error messages

- One sentence that names the condition that failed, with the same terms as the design and the XML docs.
- A message never contains key material, a full license token, or a secret. It may name a key ID or a license ID.

## Terminology

The left column is the term in `docs/design.ja.md`. Write the right column in code, docs, and tests. Add a row when the design adds a term.

| Design term | Write |
| --- | --- |
| ライセンス | license |
| ライセンスファイル、ライセンストークン | license file, license token |
| 発行者（ベンダー） | issuer (vendor) |
| 利用者（顧客） | licensee (customer) |
| 機能、フィーチャー | feature |
| ルート鍵 | root key |
| 署名鍵、検証鍵 | signing key, verification key |
| 鍵の証明書 | key certificate |
| トラストストア | trust store |
| 鍵 ID | key ID (`kid`) |
| インストール鍵 | installation key |
| マシン指紋 | machine fingerprint |
| 証明（DPoP 形式） | proof |
| ノンス | nonce |
| 回収 | reclaim |
| 借り出し | borrow |
| 更新番号 | sequence number (`seq`) |
| サーバーライセンス | server license |
| 有効期限 | expiry, expires at |
| 猶予期間 | grace period |
| 端末紐づけ、ノードロック | node lock, node-locked |
| マシン ID | machine ID |
| フローティング、同時利用数 | floating, concurrent seats |
| 貸出、リース | lease |
| 貸出の更新（ハートビート） | lease renewal (heartbeat) |
| 有効化、無効化 | activation, deactivation |
| 失効 | revocation |
| オンプレミスのライセンスサーバー | on-premises license server |
| SaaS 型の認証サーバー | hosted activation service |

Words to avoid:

| Avoid | Write |
| --- | --- |
| "secure", "unbreakable", "tamper-proof" without the threat named | the property and the threat it holds against ("an edited payload fails signature verification") |
| "fails" for throwing | "throws <see cref="InvalidOperationException"/>" (the concrete type) |
| "fast" or "zero-cost" without a number | the measurement, or nothing |
| "encrypt" for signing | "sign"; a license is signed, not encrypted, unless the design says so |

## After a review

1. Before applying a suggested replacement, look for similar sentences elsewhere (`.cursor/rules/similar-findings.mdc`).
2. If the finding is not covered above, add it here as a general rule, not as one sentence.
