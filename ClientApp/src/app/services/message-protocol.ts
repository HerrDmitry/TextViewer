/**
 * MessageProtocol — shared wire envelope encoding/decoding for the Message Bus.
 *
 * Wire format: `Message_Type\nCorrelation_ID\npayload`
 *
 * - Message_Type: [a-z0-9:-]+, 1–64 chars
 * - Correlation_ID: [a-zA-Z0-9-]+, 1–36 chars
 * - Payload: 0–2,097,152 chars, may contain newlines
 */
export class MessageProtocol {
  private static readonly MESSAGE_TYPE_REGEX = /^[a-z0-9:-]+$/;
  private static readonly CORRELATION_ID_REGEX = /^[a-zA-Z0-9-]+$/;
  private static readonly MAX_MESSAGE_TYPE_LENGTH = 64;
  private static readonly MAX_CORRELATION_ID_LENGTH = 36;
  private static readonly MAX_PAYLOAD_LENGTH = 2_097_152;

  /**
   * Encodes a message into the wire envelope format.
   * Concatenates messageType, correlationId, and payload with '\n' separator.
   * No payload or empty string payload produce identical output.
   */
  static encode(messageType: string, correlationId: string, payload?: string): string {
    return messageType + '\n' + correlationId + '\n' + (payload ?? '');
  }

  /**
   * Decodes a raw wire string into its constituent fields.
   * Splits on the first two '\n' occurrences — payload may contain additional newlines.
   * Returns null if the raw string contains fewer than 2 newline characters.
   */
  static decode(raw: string): { messageType: string; correlationId: string; payload: string } | null {
    const firstNewline = raw.indexOf('\n');
    if (firstNewline === -1) {
      return null;
    }

    const secondNewline = raw.indexOf('\n', firstNewline + 1);
    if (secondNewline === -1) {
      return null;
    }

    return {
      messageType: raw.substring(0, firstNewline),
      correlationId: raw.substring(firstNewline + 1, secondNewline),
      payload: raw.substring(secondNewline + 1),
    };
  }

  /**
   * Validates a Message_Type string.
   * Must match [a-z0-9:-]+, 1–64 chars.
   */
  static validateMessageType(type: string): boolean {
    return (
      type.length >= 1 &&
      type.length <= MessageProtocol.MAX_MESSAGE_TYPE_LENGTH &&
      MessageProtocol.MESSAGE_TYPE_REGEX.test(type)
    );
  }

  /**
   * Validates a Correlation_ID string.
   * Must match [a-zA-Z0-9-]+, 1–36 chars.
   */
  static validateCorrelationId(id: string): boolean {
    return (
      id.length >= 1 &&
      id.length <= MessageProtocol.MAX_CORRELATION_ID_LENGTH &&
      MessageProtocol.CORRELATION_ID_REGEX.test(id)
    );
  }

  /**
   * Validates a payload string.
   * Must be at most 2,097,152 characters.
   */
  static validatePayload(payload: string): boolean {
    return payload.length <= MessageProtocol.MAX_PAYLOAD_LENGTH;
  }
}
