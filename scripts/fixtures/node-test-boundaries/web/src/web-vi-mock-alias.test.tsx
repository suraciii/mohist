const { mock: mockModule } = vi
const { doMock: doMockModule } = vi
const directMock = vi.mock
const globalMock = globalThis.vi.mock

void mockModule
void doMockModule
void directMock
void globalMock
