/* global jest */

const mockStore = new Map();

jest.mock('@react-native-async-storage/async-storage', () => ({
  __esModule: true,
  default: {
    clear: jest.fn(async () => mockStore.clear()),
    getAllKeys: jest.fn(async () => Array.from(mockStore.keys())),
    getItem: jest.fn(async key => mockStore.get(key) ?? null),
    removeItem: jest.fn(async key => mockStore.delete(key)),
    setItem: jest.fn(async (key, value) => mockStore.set(key, value)),
  },
}));
