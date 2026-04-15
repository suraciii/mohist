## ADDED Requirements

### Requirement: RateLimiter class interface
The system SHALL provide a RateLimiter class that encapsulates rate limiting logic with proper lifecycle management.

#### Scenario: RateLimiter initializes with configuration
- **WHEN** a RateLimiter is instantiated with windowMs and maxRequests parameters
- **THEN** it SHALL start with an empty request map and begin periodic cleanup

#### Scenario: RateLimiter allows requests within limit
- **GIVEN** a RateLimiter with limit of 30 requests per minute
- **WHEN** check() is called 30 times with the same IP within 1 minute
- **THEN** all calls SHALL return { allowed: true }

#### Scenario: RateLimiter blocks excessive requests
- **GIVEN** a RateLimiter with limit of 30 requests per minute
- **WHEN** check() is called 31 times with the same IP within 1 minute
- **THEN** the 31st call SHALL return { allowed: false, retryAfter: <seconds> }

#### Scenario: RateLimiter tracks different IPs separately
- **GIVEN** a RateLimiter with limit of 30 requests per minute
- **WHEN** 30 requests are made from IP-A and 30 requests from IP-B
- **THEN** all requests SHALL be allowed as they are tracked independently

### Requirement: RateLimiter lifecycle management
The system SHALL provide proper cleanup mechanisms for RateLimiter to prevent memory leaks.

#### Scenario: RateLimiter disposes resources
- **GIVEN** a RateLimiter with active cleanup timer
- **WHEN** dispose() is called
- **THEN** the timer SHALL be cleared and the request map SHALL be emptied

#### Scenario: RateLimiter can be recreated after disposal
- **GIVEN** a RateLimiter has been disposed
- **WHEN** a new RateLimiter is instantiated
- **THEN** it SHALL function correctly with fresh state

### Requirement: RateLimiter cleanup behavior
The system SHALL automatically clean up expired rate limit entries.

#### Scenario: Expired entries are cleaned up
- **GIVEN** a RateLimiter with 1-minute window
- **WHEN** an IP makes requests and then no requests for 1+ minutes
- **THEN** the periodic cleanup SHALL remove the expired entry from the map

#### Scenario: Active entries are preserved
- **GIVEN** a RateLimiter with 1-minute window
- **WHEN** an IP makes requests within the window
- **THEN** the entry SHALL NOT be cleaned up during periodic cleanup
