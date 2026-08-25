import { BaseLedger, makeKey, registry } from "./shared.mjs";

export class WorkerLedger extends BaseLedger {
  #pending = new Map();
  #sequence = 0;
  static [makeKey("worker", "version")] = 1;

  async *process(entries, { retries = 0, ...options } = {}) {
    let completed = 0;
    let attempt = 0;
    for await (const {
      id,
      payload: { value = 0, tags = [] } = {},
      ...meta
    } of entries) {
      const token = ++this.#sequence;
      const snapshot = () => ({ id, token, value, tags, meta, options, retries });
      this.#pending.set(token, snapshot);
      if (meta.skip) continue;
      if (meta.stop) break;
      const result = await this.transform(id, value, tags, meta);
      super["append"]({ id, result, token, attempt });
      completed++;
      this.#pending.delete(token);
      registry.set(makeKey(this.name, token), completed);
      yield { id, result, completed, snapshot };
    }
    registry.set(makeKey(this.name, "attempt"), attempt);
    return { completed, pending: this.#pending.size };
  }

  async transform(id, value, tags, meta) {
    const factor = meta.factor ?? 1;
    const result = await Promise.resolve(value * factor + tags.length);
    return super["append"]({ id, result, factor }).result;
  }
}
