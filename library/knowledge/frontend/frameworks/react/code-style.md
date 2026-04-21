# React Code Style

Based on React.dev official documentation and community best practices.

---

## Naming

### File Names

- Use PascalCase for components: `UserProfile.tsx`
- Use camelCase for utilities/hooks: `useUserData.ts`
- Tests: `UserProfile.test.tsx` or `UserProfile.spec.tsx`

### Components

- PascalCase: `UserProfile`
- Descriptive names that reflect UI purpose

### Hooks

- Prefix with `use`: `useUserData`, `useAuth`

---

## Project Structure

- Feature-based organization:
  ```
  src/
  ├─ features/
  │ ├─ user-profile/
  │ │ ├─ components/
  │ │ ├─ hooks/
  │ │ ├─ types.ts
  │ │ └─ index.ts
  ├─ components/
  ├─ hooks/
  ├─ utils/
  ```
- Colocate tests with components

---

## Components

### Functional Components with TypeScript

```typescript
interface UserProfileProps {
  userId: string;
  onSave?: () => void;
}

export function UserProfile({ userId, onSave }: UserProfileProps) {
  // component logic
  return <div>...</div>;
}
```

### Props

- Always type props with interfaces
- Use optional props sparingly (`?`)
- Destructure props in function signature

---

## State Management

### useState

```typescript
const [count, setCount] = useState(0);
const [user, setUser] = useState<User | null>(null);
```

### useReducer

```typescript
const [state, dispatch] = useReducer(reducer, initialState);
```

### Context

```typescript
const UserContext = createContext<UserContextType>(null);

export function useUser() {
  return useContext(UserContext);
}
```

---

## Hooks Best Practices

### Custom Hooks

- Start with `use` prefix
- Return tuples for setters or objects for complex state
- Keep focused on single responsibility

```typescript
function useUserData(userId: string) {
  const [user, setUser] = useState<User | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    // fetch user
  }, [userId]);

  return { user, loading };
}
```

---

## Performance

### useMemo

```typescript
const derivedValue = useMemo(() => 
  expensiveCalculation(a, b), 
  [a, b]
);
```

### useCallback

```typescript
const handleClick = useCallback(() => {
  // handler
}, [dependencies]);
```

### React.memo

```typescript
const UserCard = React.memo(function UserCard({ user }: { user: User }) {
  return <div>{user.name}</div>;
});
```

---

## JSX Best Practices

- Always include `key` in lists
- Use conditional rendering:
  ```tsx
  {isLoading && <Spinner />}
  {data && <DataDisplay data={data} />}
  ```
- Extract repeated JSX into components
- Avoid inline event handlers in JSX

---

## Testing

- Use React Testing Library
- Test user behavior, not implementation
- Mock external dependencies

```typescript
render(<UserProfile userId="123" />);
expect(screen.getByText('John')).toBeInTheDocument();
```

---

## Best Practices

- Enable strict TypeScript
- Use TypeScript interfaces for props
- Keep components small and focused
- Use composition over inheritance
- Memoize expensive computations
