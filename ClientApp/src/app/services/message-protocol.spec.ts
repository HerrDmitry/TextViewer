import { MessageProtocol } from './message-protocol';

describe('MessageProtocol', () => {
  describe('encode', () => {
    it('should concatenate fields with newline separator', () => {
      const result = MessageProtocol.encode('open-file', 'abc-123', 'hello');
      expect(result).toBe('open-file\nabc-123\nhello');
    });

    it('should produce identical output for undefined and empty string payload', () => {
      const withUndefined = MessageProtocol.encode('test-type', 'id-1', undefined);
      const withEmpty = MessageProtocol.encode('test-type', 'id-1', '');
      expect(withUndefined).toBe(withEmpty);
      expect(withUndefined).toBe('test-type\nid-1\n');
    });

    it('should preserve newlines in payload', () => {
      const result = MessageProtocol.encode('msg', 'id', 'line1\nline2\nline3');
      expect(result).toBe('msg\nid\nline1\nline2\nline3');
    });
  });

  describe('decode', () => {
    it('should split on first two newlines', () => {
      const result = MessageProtocol.decode('open-file\nabc-123\nhello');
      expect(result).toEqual({
        messageType: 'open-file',
        correlationId: 'abc-123',
        payload: 'hello',
      });
    });

    it('should return null if no newlines present', () => {
      expect(MessageProtocol.decode('no-newlines')).toBeNull();
    });

    it('should return null if only one newline present', () => {
      expect(MessageProtocol.decode('one\nnewline')).toBeNull();
    });

    it('should preserve additional newlines in payload', () => {
      const result = MessageProtocol.decode('type\nid\npay\nload\nmore');
      expect(result).toEqual({
        messageType: 'type',
        correlationId: 'id',
        payload: 'pay\nload\nmore',
      });
    });

    it('should decode empty payload correctly', () => {
      const result = MessageProtocol.decode('type\nid\n');
      expect(result).toEqual({
        messageType: 'type',
        correlationId: 'id',
        payload: '',
      });
    });

    it('should round-trip encode then decode', () => {
      const original = { messageType: 'test:msg', correlationId: 'abc-DEF-123', payload: 'data\nwith\nnewlines' };
      const encoded = MessageProtocol.encode(original.messageType, original.correlationId, original.payload);
      const decoded = MessageProtocol.decode(encoded);
      expect(decoded).toEqual(original);
    });
  });

  describe('validateMessageType', () => {
    it('should accept valid message types', () => {
      expect(MessageProtocol.validateMessageType('open-file')).toBe(true);
      expect(MessageProtocol.validateMessageType('system:error')).toBe(true);
      expect(MessageProtocol.validateMessageType('a')).toBe(true);
      expect(MessageProtocol.validateMessageType('abc123')).toBe(true);
      expect(MessageProtocol.validateMessageType('a-b:c-d')).toBe(true);
    });

    it('should reject empty string', () => {
      expect(MessageProtocol.validateMessageType('')).toBe(false);
    });

    it('should reject strings longer than 64 chars', () => {
      expect(MessageProtocol.validateMessageType('a'.repeat(65))).toBe(false);
    });

    it('should accept exactly 64 chars', () => {
      expect(MessageProtocol.validateMessageType('a'.repeat(64))).toBe(true);
    });

    it('should reject uppercase letters', () => {
      expect(MessageProtocol.validateMessageType('Open-File')).toBe(false);
    });

    it('should reject spaces', () => {
      expect(MessageProtocol.validateMessageType('open file')).toBe(false);
    });

    it('should reject underscores', () => {
      expect(MessageProtocol.validateMessageType('open_file')).toBe(false);
    });
  });

  describe('validateCorrelationId', () => {
    it('should accept valid correlation IDs', () => {
      expect(MessageProtocol.validateCorrelationId('abc-123')).toBe(true);
      expect(MessageProtocol.validateCorrelationId('ABC-def-456')).toBe(true);
      expect(MessageProtocol.validateCorrelationId('a')).toBe(true);
      expect(MessageProtocol.validateCorrelationId('550e8400-e29b-41d4-a716-446655440000')).toBe(true);
    });

    it('should reject empty string', () => {
      expect(MessageProtocol.validateCorrelationId('')).toBe(false);
    });

    it('should reject strings longer than 36 chars', () => {
      expect(MessageProtocol.validateCorrelationId('a'.repeat(37))).toBe(false);
    });

    it('should accept exactly 36 chars', () => {
      expect(MessageProtocol.validateCorrelationId('a'.repeat(36))).toBe(true);
    });

    it('should reject colons', () => {
      expect(MessageProtocol.validateCorrelationId('abc:def')).toBe(false);
    });

    it('should reject spaces', () => {
      expect(MessageProtocol.validateCorrelationId('abc def')).toBe(false);
    });

    it('should reject underscores', () => {
      expect(MessageProtocol.validateCorrelationId('abc_def')).toBe(false);
    });
  });

  describe('validatePayload', () => {
    it('should accept empty payload', () => {
      expect(MessageProtocol.validatePayload('')).toBe(true);
    });

    it('should accept payload at max length', () => {
      expect(MessageProtocol.validatePayload('x'.repeat(2_097_152))).toBe(true);
    });

    it('should reject payload exceeding max length', () => {
      expect(MessageProtocol.validatePayload('x'.repeat(2_097_153))).toBe(false);
    });

    it('should accept payload with newlines', () => {
      expect(MessageProtocol.validatePayload('line1\nline2\nline3')).toBe(true);
    });
  });
});
