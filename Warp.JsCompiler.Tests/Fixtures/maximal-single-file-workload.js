const ledger = new Map();
const composeKey = (scope, value) => `${scope}:${value}`;

class RootEngine {
  #events = [];

  constructor(name, { mode = "normal", ...options } = {}) {
    this.name = name;
    this.mode = mode;
    this.options = options;
  }

  get events() { return this.#events; }
  set events(value) { this.#events = value; }

  ["append"](event) {
    this.#events.push(event);
    return event;
  }

  static create(name, options) {
    return new this(name, options);
  }
}

class TransformEngine extends RootEngine {
  #pending = new Map();
  #sequence = 0;
  static [composeKey("engine", "revision")] = 1;

  async *stream(records, { retries = 1, label = this.name, ...settings } = {}) {
    let completed = 0;
    outer: for await (const {
      id,
      payload: { value = 0, tags = [], nested: { factor = 1 } = {} } = {},
      ...meta
    } of records) {
      const token = ++this.#sequence;
      const snapshot = () => ({ id, token, value, tags, factor, meta, settings, label });
      this.#pending.set(token, snapshot);
      try {
        if (meta.skip) continue;
        if (meta.stop) break outer;
        let attempt = 0;
        retry: while (attempt++ <= retries) {
          try {
            const result = await this.transform(id, value, tags, factor, meta);
            super["append"]({ id, result, token, attempt });
            completed++;
            yield { id, result, completed, snapshot };
            break retry;
          } catch ({ message = "transform", code = "E_TRANSFORM", ...detail }) {
            this.lastError ??= code;
            if (attempt > retries) yield { id, code, message, detail, snapshot };
          }
        }
      } finally {
        this.#pending.delete(token);
        ledger.set(composeKey(this.name, token), completed);
      }
    }
    return { completed, pending: this.#pending.size };
  }

  async transform(id, value, tags, factor, meta) {
    const offset = meta.offset ?? 0;
    const result = await Promise.resolve(value * factor + tags.length + offset);
    return super["append"]({ id, result, factor, offset }).result;
  }
}

class AuditEngine extends TransformEngine {
  #audit = [];
  #lastError = null;
  static [composeKey("audit", "revision")] = 1;

  get lastError() { return this.#lastError; }
  set lastError(value) { this.#lastError = value; }

  *history(prefix = this.name) {
    for (const [index, event] of this.events.entries()) {
      yield { index, event, key: composeKey(prefix, index) };
    }
  }

  async runPlan(plan = [], { dryRun = false, ...options } = {}) {
    const report = [];
    let index = 0;
    for (const {
      kind = "records",
      records = [],
      settings: { retries = 0, ...streamSettings } = {},
      ...meta
    } of plan) {
      const current = ++index;
      const close = () => ({ current, kind, meta, options, size: report.length });
      try {
        switch (kind) {
          case "skip":
            continue;
          case "stop":
            break;
          default:
            if (dryRun) {
              report.push({ kind, dryRun, close });
              continue;
            }
            for await (const entry of this.stream(records, { retries, ...streamSettings })) {
              const { id, result = null, code = null, ...rest } = entry;
              this.#audit.push({ id, result, code, rest, current });
              report.push({ id, result, code, close });
            }
        }
      } catch (error) {
        const { message = "plan", ...detail } = error;
        this.lastError = message;
        report.push({ message, detail, close });
      } finally {
        ledger.set(composeKey(this.name, current), report.length);
      }
    }
    return { report, audit: this.#audit, history: [...this.history("plan")] };
  }

  static {
    this[composeKey("audit", "revision")] += 1;
  }
}

async function* input(groups) {
  for (const [id, value = 0, tags = [], factor = 1] of groups) {
    yield Promise.resolve({ id, payload: { value, tags, nested: { factor } } });
  }
}

export async function execute(groups, { name = "maximum", mode = "normal" } = {}) {
  const engine = AuditEngine.create(name, { mode });
  const plan = [{ kind: "records", records: input(groups), settings: { retries: mode === "retry" ? 2 : 0 } }];
  try {
    return await engine.runPlan(plan, { dryRun: false, mode });
  } finally {
    ledger.set(composeKey(name, "events"), engine.events.length);
  }
}

export { AuditEngine, TransformEngine, ledger };
