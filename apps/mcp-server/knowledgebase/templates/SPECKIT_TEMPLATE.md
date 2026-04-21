# Speckit Template (Single Source of Truth)

A Speckit is the single source of truth for a feature, enabling product, engineering, QA, and operations to execute from one structured specification.

## Lifecycle

Idea -> Product Spec -> Engineering Design -> Test Spec -> DevOps Release Spec

## Sections in This Folder

1. [PRODUCT_SPEC_TEMPLATE.md](PRODUCT_SPEC_TEMPLATE.md)
2. [ENGINEERING_DESIGN_TEMPLATE.md](ENGINEERING_DESIGN_TEMPLATE.md)
3. [QA_TEST_SPEC_TEMPLATE.md](QA_TEST_SPEC_TEMPLATE.md)
4. [RELEASE_SPEC_TEMPLATE.md](RELEASE_SPEC_TEMPLATE.md)

## Suggested Usage with Workflow Commands

- `/spec`: Start from Product Spec Template.
- `/architect`: Use Engineering Design Template.
- `/test`: Use QA Test Spec Template.
- `/commit`: Include release plan and rollback from Release Spec Template.

## Minimum Required Artifacts for Feature Readiness

- Product spec with acceptance criteria.
- Engineering design with contracts/data model changes.
- Test spec with coverage and exit criteria.
- Release spec with deployment strategy, rollback, and observability.
