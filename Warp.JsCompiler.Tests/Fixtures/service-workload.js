const defaults = { retries: 1, labels: ["service"] };

function mergeOptions(options = {}) {
    return {
        ...defaults,
        ...options,
        labels: [...defaults.labels, ...(options.labels || [])],
    };
}

class BaseService {
    constructor(name) {
        this.name = name;
        this.events = [];
    }

    record(event) {
        this.events.push(event);
        return event;
    }
}

class Service extends BaseService {
    #options;

    constructor(name, options) {
        super(name);
        this.#options = mergeOptions(options);
    }

    async execute({ id, payload = 0 }, context) {
        const makeEvent = value => ({ id, value, service: this.name });
        try {
            const value = await context.transform(payload, this.#options);
            return this.record(makeEvent(value));
        } catch (error) {
            return this.record({ id, error: String(error) });
        } finally {
            context.lastService = this.name;
        }
    }
}

const service = new Service("metrics", { retries: 2, labels: ["batch"] });
globalThis.executeService = (request, context) => service.execute(request, context);
