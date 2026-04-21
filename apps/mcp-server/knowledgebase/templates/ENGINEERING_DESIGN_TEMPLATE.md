# Engineering / Technical Design Template

## Architecture Overview
Describe the system design.

Include:
- Service architecture
- Data flow
- Components

## System Components
- Component A:
- Component B:
- Component C:

## API Contracts
Example:

POST /api/v1/roster/upload

Request
```json
{
  "schoolId": "123",
  "file": "csv"
}
```

Response
```json
{
  "status": "success",
  "studentsCreated": 25
}
```

## Data Model Changes
- New entities/tables/collections:
- Schema changes:
- Migration plan:
- Backward compatibility:

## Integration Points
- Authentication service
- User database
- Messaging system
- Other:

## Security Considerations
- RBAC permissions
- Input validation
- Encryption
- Secret handling

## Performance Requirements
- Throughput target:
- Latency target:
- Capacity target:

Example:
- Handle 10k students per upload
- API response under 500ms

## Failure Handling
- Retry strategy
- Dead-letter queue
- Error logging
- Fallback behavior

## Technical Dependencies
- Service dependencies:
- Data dependencies:
- Infrastructure dependencies:

## Implementation Tasks (DAG)
- TASK-1:
- TASK-2 (depends on TASK-1):
- TASK-3:
