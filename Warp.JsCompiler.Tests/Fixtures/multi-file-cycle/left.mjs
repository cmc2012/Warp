import { cache, key, select } from "./common.mjs";
import "./right.mjs";

export class LeftProcessor {
  #count = 0;
  #history = [];

  constructor(name = "left") {
    this.name = name;
  }

  async process(input = {}) {
    const { value, tags, meta, score } = select(input);
    const token = ++this.#count;
    const snapshot = () => ({ token, value, tags, meta, score, name: this.name });
    try {
      const result = await Promise.resolve(score * (meta.factor ?? 1));
      this.#history.push({ result, snapshot });
      return { result, token, snapshot };
    } finally {
      cache.set(key(this.name, token), this.#history.length);
    }
  }

  *entries() {
    yield* this.#history;
  }
}
