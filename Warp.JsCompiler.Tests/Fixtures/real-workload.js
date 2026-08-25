const defaultPolicy = {
    retry: 2,
    tags: ["interactive"],
};

function createPolicy(overrides = {}) {
    const policy = { ...defaultPolicy, ...overrides };
    policy.tags = [...defaultPolicy.tags, ...(overrides.tags || [])];
    return policy;
}

class WorkQueue {
    #pending = [];

    constructor(name, policy = {}) {
        this.name = name;
        this.policy = createPolicy(policy);
        this.completed = [];
    }

    enqueue(task) {
        this.#pending.push(task);
        return this.#pending.length;
    }

    async drain(context) {
        const report = { ok: 0, failed: 0, values: [] };
        while (this.#pending.length) {
            const task = this.#pending.shift();
            try {
                const value = await task(context);
                report.ok++;
                report.values.push(value);
                this.completed.push(value);
            } catch (error) {
                report.failed++;
                report.values.push({ error: String(error) });
            } finally {
                context.lastQueue = this.name;
            }
        }
        return report;
    }
}

function createService(name, options) {
    const queue = new WorkQueue(name, options);
    return {
        submit(payload) {
            return queue.enqueue(async context => {
                const { id, value = 0 } = payload;
                return { id, value: value + context.offset };
            });
        },
        flush(context) {
            return queue.drain(context);
        },
    };
}

const service = createService("metrics", { tags: ["batch"], retry: 1 });
service.submit({ id: "first", value: 2 });
service.submit({ id: "second" });
globalThis.runMetrics = context => service.flush(context);
