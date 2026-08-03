export type PetInteractionLeaseHost = {
  activate(): Promise<void>;
  deactivate(): Promise<void>;
  reportError(error: unknown): void;
};

type ActiveLease = {
  generation: number;
  activation: Promise<void>;
};

export class PetInteractionLease {
  private active: ActiveLease | null = null;
  private nextGeneration = 0;

  constructor(private readonly host: PetInteractionLeaseHost) {}

  begin(): void {
    let activation: Promise<void>;
    try {
      activation = Promise.resolve(this.host.activate());
    } catch (error) {
      activation = Promise.reject(error);
    }
    const lease = { generation: ++this.nextGeneration, activation };
    this.active = lease;
    void activation.catch((error) => this.host.reportError(error));
  }

  wait(): Promise<void> {
    if (!this.active) return Promise.reject(new Error("Pet interaction lease was not acquired"));
    return this.active.activation;
  }

  async finish(): Promise<void> {
    const lease = this.active;
    if (!lease) return;

    try {
      await lease.activation;
    } catch {
      // Activation failures are reported in begin; cleanup must still run.
    }
    if (this.active !== lease) return;

    this.active = null;
    await this.host.deactivate();
  }
}
