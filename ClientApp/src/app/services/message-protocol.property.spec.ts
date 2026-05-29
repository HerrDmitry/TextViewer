/**
 * Feature: message-bus-service, Property 1: Protocol round-trip
 *
 * Validates: Requirements 8.1, 8.2, 8.3, 8.4, 8.7
 *
 * Property: For any valid Message_Type, Correlation_ID, and payload (including
 * payloads containing newlines and unicode), encoding then decoding SHALL produce
 * values identical to the original inputs.
 */
import * as fc from 'fast-check';
import { MessageProtocol } from './message-protocol';

const messageTypeChars = 'abcdefghijklmnopqrstuvwxyz0123456789:-';
const correlationIdChars = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-';

/** Generator for valid Message_Type: [a-z0-9:-]+, 1–64 chars */
const validMessageType = fc
  .integer({ min: 1, max: 64 })
  .chain((len) =>
    fc.string({ minLength: len, maxLength: len, unit: fc.constantFrom(...messageTypeChars.split('')) })
  );

/** Generator for valid Correlation_ID: [a-zA-Z0-9-]+, 1–36 chars */
const validCorrelationId = fc
  .integer({ min: 1, max: 36 })
  .chain((len) =>
    fc.string({ minLength: len, maxLength: len, unit: fc.constantFrom(...correlationIdChars.split('')) })
  );

/** Generator for payload: any string including newlines and unicode, 0–1000 chars */
const validPayload = fc.string({ minLength: 0, maxLength: 1000 });

describe('Feature: message-bus-service, Property 1: Protocol round-trip', () => {
  it('decode(encode(type, id, payload)) produces values identical to inputs', () => {
    fc.assert(
      fc.property(
        validMessageType,
        validCorrelationId,
        validPayload,
        (messageType, correlationId, payload) => {
          const encoded = MessageProtocol.encode(messageType, correlationId, payload);
          const decoded = MessageProtocol.decode(encoded);

          expect(decoded).not.toBeNull();
          expect(decoded!.messageType).toBe(messageType);
          expect(decoded!.correlationId).toBe(correlationId);
          expect(decoded!.payload).toBe(payload);
        }
      ),
      { numRuns: 100 }
    );
  });
});

/**
 * Feature: message-bus-service, Property 2: No-payload equivalence
 *
 * Validates: Requirements 2.4, 8.5, 8.6
 *
 * Property: For any valid Message_Type and Correlation_ID, encoding with
 * `undefined`/no payload SHALL produce an identical wire string as encoding
 * with empty string `""`.
 */
describe('Feature: message-bus-service, Property 2: No-payload equivalence', () => {
  it('encode(type, id, undefined) produces identical output to encode(type, id, "")', () => {
    fc.assert(
      fc.property(
        validMessageType,
        validCorrelationId,
        (messageType, correlationId) => {
          const withUndefined = MessageProtocol.encode(messageType, correlationId, undefined);
          const withEmptyString = MessageProtocol.encode(messageType, correlationId, '');

          expect(withUndefined).toBe(withEmptyString);
        }
      ),
      { numRuns: 100 }
    );
  });
});

/**
 * Feature: message-bus-service, Property 14: Validation rejects invalid fields
 *
 * Validates: Requirements 8.9, 8.10, 15.1, 15.2, 15.3, 15.4, 15.5, 15.6, 15.7
 *
 * Property: For any Message_Type not matching [a-z0-9:-]+ (1–64 chars), or
 * Correlation_ID not matching [a-zA-Z0-9-]+ (1–36 chars), or payload exceeding
 * 2,097,152 chars, the validation SHALL reject the input.
 */
describe('Feature: message-bus-service, Property 14: Validation rejects invalid fields', () => {
  // Characters that are invalid in message types
  const invalidMsgTypeChars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ _!@#$%^&*()+=~`[]{}|\\;\'"<>,./?\t\n';

  // Characters that are invalid in correlation IDs (colons, spaces, underscores, special)
  const invalidCorrIdChars = ':_ !@#$%^&*()+=~`[]{}|\\;\'"<>,./?\t\n';

  // --- validateMessageType ---

  describe('validateMessageType rejects invalid', () => {
    // Generator: empty string
    const emptyString = fc.constant('');

    // Generator: contains at least one invalid character
    const withInvalidChar = fc
      .tuple(
        fc.string({ minLength: 0, maxLength: 30, unit: fc.constantFrom(...messageTypeChars.split('')) }),
        fc.constantFrom(...invalidMsgTypeChars.split('')),
        fc.string({ minLength: 0, maxLength: 30, unit: fc.constantFrom(...messageTypeChars.split('')) })
      )
      .map(([prefix, bad, suffix]) => prefix + bad + suffix);

    // Generator: oversized (>64 chars) but otherwise valid characters
    const oversized = fc
      .integer({ min: 65, max: 128 })
      .chain((len) =>
        fc.string({ minLength: len, maxLength: len, unit: fc.constantFrom(...messageTypeChars.split('')) })
      );

    const invalidMessageType = fc.oneof(emptyString, withInvalidChar, oversized);

    it('rejects strings with invalid characters, empty, or oversized', () => {
      fc.assert(
        fc.property(invalidMessageType, (type) => {
          expect(MessageProtocol.validateMessageType(type)).toBe(false);
        }),
        { numRuns: 100 }
      );
    });
  });

  describe('validateMessageType accepts valid', () => {
    it('accepts strings matching [a-z0-9:-]+, 1-64 chars', () => {
      fc.assert(
        fc.property(validMessageType, (type) => {
          expect(MessageProtocol.validateMessageType(type)).toBe(true);
        }),
        { numRuns: 100 }
      );
    });
  });

  // --- validateCorrelationId ---

  describe('validateCorrelationId rejects invalid', () => {
    // Generator: empty string
    const emptyString = fc.constant('');

    // Generator: contains at least one invalid character
    const withInvalidChar = fc
      .tuple(
        fc.string({ minLength: 0, maxLength: 16, unit: fc.constantFrom(...correlationIdChars.split('')) }),
        fc.constantFrom(...invalidCorrIdChars.split('')),
        fc.string({ minLength: 0, maxLength: 16, unit: fc.constantFrom(...correlationIdChars.split('')) })
      )
      .map(([prefix, bad, suffix]) => prefix + bad + suffix);

    // Generator: oversized (>36 chars) but otherwise valid characters
    const oversized = fc
      .integer({ min: 37, max: 72 })
      .chain((len) =>
        fc.string({ minLength: len, maxLength: len, unit: fc.constantFrom(...correlationIdChars.split('')) })
      );

    const invalidCorrelationId = fc.oneof(emptyString, withInvalidChar, oversized);

    it('rejects strings with invalid characters, empty, or oversized', () => {
      fc.assert(
        fc.property(invalidCorrelationId, (id) => {
          expect(MessageProtocol.validateCorrelationId(id)).toBe(false);
        }),
        { numRuns: 100 }
      );
    });
  });

  describe('validateCorrelationId accepts valid', () => {
    it('accepts strings matching [a-zA-Z0-9-]+, 1-36 chars', () => {
      fc.assert(
        fc.property(validCorrelationId, (id) => {
          expect(MessageProtocol.validateCorrelationId(id)).toBe(true);
        }),
        { numRuns: 100 }
      );
    });
  });

  // --- validatePayload ---

  describe('validatePayload rejects oversized', () => {
    // Generator for oversized payloads: >2,097,152 chars
    const oversizedLength = fc.integer({ min: 2_097_153, max: 2_097_200 });

    it('rejects strings exceeding 2,097,152 chars', () => {
      fc.assert(
        fc.property(oversizedLength, (len) => {
          const payload = 'x'.repeat(len);
          expect(MessageProtocol.validatePayload(payload)).toBe(false);
        }),
        { numRuns: 100 }
      );
    });
  });

  describe('validatePayload accepts valid', () => {
    // Use smaller sizes for practical test execution, plus exact boundary
    const validPayloadLength = fc.oneof(
      fc.integer({ min: 0, max: 1000 }),
      fc.constant(2_097_152) // exact boundary
    );

    it('accepts strings of length 0 to 2,097,152 chars', () => {
      fc.assert(
        fc.property(validPayloadLength, (len) => {
          const payload = 'a'.repeat(len);
          expect(MessageProtocol.validatePayload(payload)).toBe(true);
        }),
        { numRuns: 100 }
      );
    });
  });
});
