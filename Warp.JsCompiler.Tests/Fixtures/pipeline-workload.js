const policies = { retry: false, tags: [] };

function normalizePolicy(policy = {}) {
    return { ...policies, ...policy, tags: [...policies.tags, ...(policy.tags || [])] };
}

class Pipeline {
    #completed = 0;

    constructor(name, policy) {
        this.name = name;
        this.policy = normalizePolicy(policy);
    }

    async process(items, worker) {
        const results = [];
        for (const item of items) {
            try {
                const value = await worker.transform(item, this.policy);
                this.#completed++;
                results.push({ item, value, completed: this.#completed });
            } catch ({ message }) {
                results.push({ item, error: message });
            } finally {
                worker.lastPipeline = this.name;
            }
        }
        return results;
    }
}

const pipeline = new Pipeline("events", { tags: ["batch"] });
globalThis.processEvents = (items, worker) => pipeline.process(items, worker);
