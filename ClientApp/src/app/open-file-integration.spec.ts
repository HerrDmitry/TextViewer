/**
 * Integration tests for the migrated open-file flow.
 *
 * Tests the full round-trip using a real MessageBusClient instance
 * (with mocked bridge), verifying Ctrl+O → send → response → display.
 *
 * Validates: Requirements 9.1, 9.2, 9.3, 9.4, 9.5
 */

// Mock Angular core to avoid ESM transform issues in Jest
jest.mock('@angular/core', () => ({
  Injectable: () => (target: any) => target,
  OnDestroy: class {},
}));

// Polyfill crypto.randomUUID for jsdom
let uuidCounter = 0;
Object.defineProperty(globalThis, 'crypto', {
  value: {
    ...globalThis.crypto,
    randomUUID: () => {
      uuidCounter++;
      const hex = uuidCounter.toString(16).padStart(12, '0');
      return `00000000-0000-4000-8000-${hex}`;
    },
  },
  configurable: true,
});

import * as fs from 'fs';
import * as path from 'path';
import { MessageBusClient } from './services/message-bus-client.service';
import { MessageProtocol } from './services/message-protocol';
import { InboundMessage, SubscriptionHandle } from './services/message-bus.types';

describe('Open-file integration — full round-trip via real MessageBusClient', () => {
  let sendMessageSpy: jest.Mock;
  let receiveMessageCallback: ((raw: string) => void) | null;
  let client: MessageBusClient;

  beforeEach(() => {
    sendMessageSpy = jest.fn();
    receiveMessageCallback = null;

    // Mock window.external — receiveMessage is a function that captures the callback
    Object.defineProperty(window, 'external', {
      value: {
        sendMessage: sendMessageSpy,
        receiveMessage: (cb: (raw: string) => void) => {
          receiveMessageCallback = cb;
        },
      },
      writable: true,
      configurable: true,
    });

    client = new MessageBusClient();
  });

  afterEach(() => {
    client.ngOnDestroy();
  });

  /**
   * Simulates the AppComponent logic using a real MessageBusClient.
   */
  function createComponentWithRealBus() {
    let displayText = 'Hello World';
    let pendingCorrelationId: string | null = null;

    const subscription = client.subscribe('open-file', (msg: InboundMessage) => {
      if (msg.payload !== '') {
        displayText = msg.payload;
      }
      pendingCorrelationId = null;
    });

    return {
      get displayText() { return displayText; },
      get pendingCorrelationId() { return pendingCorrelationId; },
      triggerCtrlO() {
        if (pendingCorrelationId !== null) return;
        pendingCorrelationId = client.send('open-file');
      },
      destroy() {
        subscription.unsubscribe();
      },
    };
  }

  /**
   * Simulates an inbound response from the backend by invoking the
   * receiveMessage callback with a properly encoded envelope.
   */
  function simulateResponse(correlationId: string, payload: string): void {
    const envelope = MessageProtocol.encode('open-file', correlationId, payload);
    receiveMessageCallback!(envelope);
  }

  // --- Req 9.1, 9.2: Full round-trip ---

  it('Ctrl+O sends via real MessageBusClient, response updates display', async () => {
    const component = createComponentWithRealBus();

    // Trigger Ctrl+O
    component.triggerCtrlO();

    // Wait for outbound dispatch microtask
    await Promise.resolve();

    // Verify send was called on the bridge
    expect(sendMessageSpy).toHaveBeenCalledTimes(1);
    const sentEnvelope = sendMessageSpy.mock.calls[0][0] as string;
    const decoded = MessageProtocol.decode(sentEnvelope);
    expect(decoded).not.toBeNull();
    expect(decoded!.messageType).toBe('open-file');
    expect(decoded!.payload).toBe('');

    // Simulate backend response
    simulateResponse(decoded!.correlationId, '/home/user/document.txt');

    // Wait for inbound delivery microtask
    await Promise.resolve();

    expect(component.displayText).toBe('/home/user/document.txt');
    expect(component.pendingCorrelationId).toBeNull();

    component.destroy();
  });

  // --- Req 9.3: Guard prevents duplicate sends ---

  it('guard prevents duplicate sends while awaiting response', async () => {
    const component = createComponentWithRealBus();

    // First Ctrl+O — sends
    component.triggerCtrlO();
    expect(component.pendingCorrelationId).not.toBeNull();

    // Wait for outbound dispatch microtask
    await Promise.resolve();
    expect(sendMessageSpy).toHaveBeenCalledTimes(1);

    // Second Ctrl+O — blocked by guard
    component.triggerCtrlO();
    await Promise.resolve();
    expect(sendMessageSpy).toHaveBeenCalledTimes(1); // still 1

    // Resolve the pending request
    const decoded = MessageProtocol.decode(sendMessageSpy.mock.calls[0][0]);
    simulateResponse(decoded!.correlationId, '/some/file.txt');
    await Promise.resolve();

    // Now guard is cleared — third Ctrl+O should send
    component.triggerCtrlO();
    await Promise.resolve();
    expect(sendMessageSpy).toHaveBeenCalledTimes(2);

    component.destroy();
  });

  // --- Req 9.4: Empty response leaves display unchanged ---

  it('empty response leaves display unchanged', async () => {
    const component = createComponentWithRealBus();

    component.triggerCtrlO();
    expect(component.displayText).toBe('Hello World');

    // Wait for outbound dispatch microtask
    await Promise.resolve();

    const decoded = MessageProtocol.decode(sendMessageSpy.mock.calls[0][0]);
    simulateResponse(decoded!.correlationId, '');

    await Promise.resolve();

    expect(component.displayText).toBe('Hello World');
    expect(component.pendingCorrelationId).toBeNull();

    component.destroy();
  });

  // --- Req 9.1: No direct window.external.sendMessage in AppComponent source ---

  it('no direct window.external.sendMessage calls in AppComponent', () => {
    const source = fs.readFileSync(
      path.resolve(__dirname, './app.component.ts'), 'utf-8'
    );
    expect(source).not.toContain('window.external.sendMessage');
  });
});
