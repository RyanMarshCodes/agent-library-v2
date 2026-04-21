# Angular Code Style

Based on Angular.dev [Coding Style Guide](https://angular.dev/style-guide).

---

## Naming

### File Names

- Use **kebab-case** for file names: `user-profile.ts`
- Use `.spec.ts` suffix for unit tests: `user-profile.spec.ts`
- Match file names to the TypeScript identifier within
- For components with template/styles: `user-profile.ts`, `user-profile.html`, `user-profile.css`

### Classes

- Use PascalCase for class names: `UserProfile`
- Match class name to file name

### Selectors

- Use custom element names with dashes: `user-profile`
- Use application-specific prefix: `app-` or `yt-` (e.g., `yt-menu`)
- **Never use `ng` prefix** - reserved for Angular framework
- For attribute selectors: camelCase: `[appTooltip]`

---

## Project Structure

- All application code in `src/` directory
- Bootstrap in `main.ts` directly inside `src`
- Group related files (component + template + styles) in same directory
- Tests in same directory as code-under-test
- Organize by **feature areas**, not by type:
  ```
  src/
  ├─ movie-reel/
  │ ├─ show-times/
  │ ├─ reserve-tickets/
  ```
- Avoid: `components/`, `directives/`, `services/` directories

---

## Components

### Definition

```typescript
@Component({
  selector: 'user-profile',
  // standalone: true is default in Angular v20+
  templateUrl: 'user-profile.html',
  styleUrl: 'user-profile.css',
  imports: [CommonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class.active]': 'isActive',
    '(click)': 'handleClick()',
  },
})
export class UserProfile {
  // Use host object instead of @HostBinding/@HostListener
}
```

### Member Ordering

Group Angular-specific properties near the top:
1. Injected dependencies (`inject()`)
2. Inputs (`input`, `input.required`)
3. Outputs (`output`)
4. Queries (`viewChild`, `viewChildren`)
5. Public properties
6. Methods

### Selectors

| Type | Example | Usage |
|------|---------|-------|
| Type | `profile-photo` | Most common - custom elements |
| Attribute | `[dropzone]` | Extend native elements |
| Class | `.menu-item` | Avoid |

### Directive Selectors

- Use same prefix as components
- For attribute selectors, use camelCase: `[appTooltip]`

### Lifecycle

- Keep lifecycle methods simple - delegate to well-named methods
- Implement lifecycle interfaces: `OnInit`, `OnDestroy`, etc.

```typescript
// Use lifecycle interfaces to ensure correct method names
export class UserProfile implements OnInit, OnDestroy {
  ngOnInit() {
    this.loadUserData();
  }

  ngOnDestroy() {
    this.cleanup();
  }
}
```

### Focus on Presentation

- Keep components focused on presentation
- Extract business logic, validation, and data transformations to separate files/services

### Inline Templates

- Prefer inline templates for small components

```typescript
// Prefer
ngOnInit() {
  this.loadUserData();
  this.setupEventListeners();
}

// Avoid
ngOnInit() {
  this.logger.setMode('info');
  this.logger.monitorErrors();
  // ...complex logic
}
```

---

## Signals

### Input Signals

```typescript
// Basic input
value = input(0);

// Required input
userId = input.required<string>();

// With transform
disabled = input(false, { transform: booleanAttribute });

// Alias
label = input('', { alias: 'sliderLabel' });
```

### Model Signals

```typescript
// For two-way binding
value = model(0);

// Update value
this.value.set(42);
this.value.update(v => v + 1);
```

### Computed

```typescript
fullName = computed(() => `${this.firstName()} ${this.lastName()}`);
```

---

## Dependency Injection

Prefer `inject()` over constructor injection:

```typescript
// Prefer
export class UserProfile {
  private userService = inject(UserService);
}

// Avoid
constructor(private userService: UserService) {}
```

---

## Templates

### Bindings

- Prefer `class` and `style` over `ngClass` and `ngStyle`:

```html
<!-- Prefer -->
<div [class.active]="isActive" [style.color]="color">

<!-- Avoid -->
<div [ngClass]="{active: isActive}" [ngStyle]="{'color': color}">
```

### Control Flow

- Use native control flow: `@if`, `@for`, `@switch` instead of `*ngIf`, `*ngFor`, `*ngSwitch`

### Paths

- When using external templates/styles, use paths **relative to the component TS file**

### Globals

- Do not assume globals like `new Date()` are available in templates

### Async Pipe

- Use async pipe to handle observables

### Event Handlers

Name for what they do, not the event:

```html
<!-- Prefer -->
<button (click)="saveUser()">Save</button>

<!-- Avoid -->
<button (click)="handleClick()">Save</button>
```

### Accessibility

- Use meaningful element names
- Include ARIA attributes when needed

---

## Styling

### View Encapsulation

| Mode | Description |
|------|-------------|
| `Emulated` (default) | Scoped to component |
| `ShadowDom` | Native Shadow DOM |
| `None` | Global styles |

### Pseudo-classes

- Use `:host` for host element styling
- Avoid `::ng-deep` - strongly discouraged

---

## State Management

- Use signals for local component state
- Use `computed()` for derived state
- Keep state transformations pure
- Do NOT use `mutate` on signals - use `update` or `set` instead

## Forms

- Prefer Reactive forms over Template-driven forms

## Best Practices

- Use `NgOptimizedImage` (`ngSrc`) for static images (not inline base64)
- Use `readonly` for Angular-initialized properties:
  ```typescript
  readonly userId = input.required<string>();
  readonly userSaved = output<void>();
  ```
- Use `protected` for template-only members
- One concept per file
- Prefer smaller, focused components
- Test files alongside source files

## Accessibility

- MUST pass all AXE checks
- MUST follow WCAG AA minimums (focus management, color contrast, ARIA attributes)
- Use semantic HTML elements
- Ensure keyboard navigation works

## Performance

- Use `OnPush` change detection strategy
- Use `NgOptimizedImage` (`ngSrc`) for static images (not inline base64)
- Lazy load routes and components
- Avoid complex pipes - use computed signals

## TypeScript

- Use strict type checking
- Prefer type inference when obvious
- Avoid `any`; use `unknown` when uncertain
